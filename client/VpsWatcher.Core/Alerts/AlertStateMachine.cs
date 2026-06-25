using VpsWatcher.Core.Configuration;
using VpsWatcher.Core.Logging;
using VpsWatcher.Core.Schema;
using VpsWatcher.Core.Ssh;

namespace VpsWatcher.Core.Alerts;

/// <summary>
/// Per-server alert state machine (design §5.3 / §6 / 付録B). Pure logic: it turns the metric stream
/// (and connection state) into an overall <see cref="AlertLevel"/> and decides whether a voice alert
/// should sound — it does NOT play audio or touch any UI (that is a later phase, which subscribes to
/// <see cref="AlertTriggered"/>).
///
/// Anti-flap (§6.3, constants in <see cref="AlertTuning"/>):
/// <list type="bullet">
///   <item><b>Hysteresis</b>: promote when value &gt; entry; demote only when value &lt; entry − 5pt.</item>
///   <item><b>Debounce</b>: promotion needs 3 consecutive over-threshold samples; demotion is immediate.</item>
///   <item><b>Cooldown</b>: a voice for a given level is suppressed for 5 min, but escalation to a
///         HIGHER level pierces it (悪化は見逃さない / 振動は黙らせる, §6.4).</item>
/// </list>
/// Aggregation is worst-of with priority HostKeyMismatch &gt; Disconnected &gt; Critical &gt; … &gt; Normal,
/// realised as <c>Math.Max</c> over <see cref="AlertLevel"/>'s ordering.
/// </summary>
public sealed class AlertStateMachine
{
    private sealed class MetricTracker
    {
        public AlertLevel Level;
        public int EscalateCount;
        public double LastValue;
    }

    private readonly Func<DateTime> _clock;
    private readonly IAppLogger? _logger;

    // Resolved [caution, warning, critical] entry thresholds, or null when the metric isn't judged.
    private readonly double[]? _cpuTh;
    private readonly double[]? _memTh;
    private readonly double[]? _swapTh;
    private readonly double[]? _diskTh;

    private readonly MetricTracker? _cpu;
    private readonly MetricTracker? _mem;
    private readonly MetricTracker? _swap;
    private readonly Dictionary<string, MetricTracker> _disks = new(StringComparer.Ordinal);

    private AlertLevel _connection = AlertLevel.Normal; // Connected/Connecting ⇒ judge by metrics
    private AlertLevel _metricWorst = AlertLevel.Normal;
    private string? _metricCause;
    private double? _metricValue;

    private readonly Dictionary<AlertLevel, DateTime> _lastFired = new();

    public AlertStateMachine(
        string serverId, ServerThresholds? thresholds, IAppLogger? logger = null, Func<DateTime>? clock = null)
    {
        ServerId = serverId ?? throw new ArgumentNullException(nameof(serverId));
        _logger = logger;
        _clock = clock ?? (() => DateTime.UtcNow);

        _cpuTh = Resolve(thresholds?.Cpu);
        _memTh = Resolve(thresholds?.Mem);
        _swapTh = Resolve(thresholds?.Swap);
        _diskTh = Resolve(thresholds?.Disk);

        _cpu = _cpuTh is null ? null : new MetricTracker();
        _mem = _memTh is null ? null : new MetricTracker();
        _swap = _swapTh is null ? null : new MetricTracker();
    }

    public string ServerId { get; }

    /// <summary>Current overall state (worst-of metrics, then connection priority).</summary>
    public AlertLevel State { get; private set; } = AlertLevel.Normal;

    // ───────── per-metric levels (read-only view for the UI, §6 metric colours) ─────────
    // The View colours each metric's number by its own band. These expose the trackers' current
    // level without changing any judgement (Phase 5 logic untouched); a metric that isn't judged
    // (no thresholds configured) reports Normal. Read on the UI thread right after ProcessSample.

    /// <summary>Current CPU band (Normal when CPU isn't judged).</summary>
    public AlertLevel CpuLevel => _cpu?.Level ?? AlertLevel.Normal;

    /// <summary>Current memory band (Normal when memory isn't judged).</summary>
    public AlertLevel MemLevel => _mem?.Level ?? AlertLevel.Normal;

    /// <summary>Current swap band (Normal when swap isn't judged).</summary>
    public AlertLevel SwapLevel => _swap?.Level ?? AlertLevel.Normal;

    /// <summary>Current band for a disk mount (Normal when unknown / disk isn't judged).</summary>
    public AlertLevel DiskLevel(string mount)
        => _disks.TryGetValue(mount, out var t) ? t.Level : AlertLevel.Normal;

    public event EventHandler<AlertStateChangedEventArgs>? StateChanged;
    public event EventHandler<AlertTriggeredEventArgs>? AlertTriggered;

    /// <summary>Feeds one metric sample (only on a live connection). Updates each judged metric's band
    /// (with hysteresis + debounce), recomputes the metric worst-of, then the overall state.</summary>
    public void ProcessSample(Sample sample)
    {
        // CPU is a rate field: null = measuring (§3) ⇒ skip, never treat as 0.
        if (_cpu is not null && _cpuTh is not null && sample.CpuPct is { } cpu)
            UpdateMetric(_cpu, cpu, _cpuTh);

        if (_mem is not null && _memTh is not null)
            UpdateMetric(_mem, sample.Mem.UsedPct, _memTh);

        if (_swap is not null && _swapTh is not null)
            UpdateMetric(_swap, sample.Swap.UsedPct, _swapTh);

        if (_diskTh is not null)
            UpdateDisks(sample.Disk);

        RecomputeMetricWorst();
        ApplyOverall();
    }

    /// <summary>Feeds a connection-state transition. Disconnected/HostKeyMismatch outrank metrics; on
    /// entering them the debounce counters are reset so recovery starts clean (§6.3).</summary>
    public void ProcessConnectionState(ConnectionState state)
    {
        var mapped = state switch
        {
            ConnectionState.HostKeyMismatch => AlertLevel.HostKeyMismatch,
            ConnectionState.Disconnected => AlertLevel.Disconnected,
            _ => AlertLevel.Normal, // Connecting / Connected ⇒ judge by metrics
        };

        if (mapped is AlertLevel.Disconnected or AlertLevel.HostKeyMismatch)
            ResetDebounce();

        _connection = mapped;
        ApplyOverall();
    }

    // ───────────────────────── metric classification ─────────────────────────

    private static double[]? Resolve(IReadOnlyList<double>? t)
        => t is { Count: >= 3 } ? new[] { t[0], t[1], t[2] } : null;

    /// <summary>Instantaneous band for a value given the current band: promote on value &gt; entry,
    /// demote on value &lt; entry − margin (design §6.3 / 付録B classify_with_hysteresis).</summary>
    private static int Classify(double value, int current, double[] entry)
    {
        int level = current;
        while (level < 3 && value > entry[level]) level++;                              // escalate
        while (level > 0 && value < entry[level - 1] - AlertTuning.HysteresisMarginPct) level--; // de-escalate
        return level;
    }

    private static void UpdateMetric(MetricTracker t, double value, double[] entry)
    {
        t.LastValue = value;
        int target = Classify(value, (int)t.Level, entry);

        if (target > (int)t.Level)
        {
            // Promotion is debounced: only after N consecutive over-threshold samples.
            t.EscalateCount++;
            if (t.EscalateCount >= AlertTuning.DebounceSamples)
            {
                t.Level = (AlertLevel)target;
                t.EscalateCount = 0;
            }
        }
        else
        {
            // Same band or below ⇒ demotion is immediate (no debounce); the streak resets.
            t.Level = (AlertLevel)target;
            t.EscalateCount = 0;
        }
    }

    private void UpdateDisks(IReadOnlyList<DiskEntry> disks)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var d in disks)
        {
            seen.Add(d.Mount);
            if (!_disks.TryGetValue(d.Mount, out var tracker))
            {
                tracker = new MetricTracker();
                _disks[d.Mount] = tracker;
            }
            UpdateMetric(tracker, d.UsedPct, _diskTh!);
        }

        // Drop mounts no longer reported so a vanished disk can't pin the worst-of.
        if (_disks.Count != seen.Count)
        {
            foreach (var mount in _disks.Keys.Where(k => !seen.Contains(k)).ToList())
                _disks.Remove(mount);
        }
    }

    // ───────────────────────── aggregation ─────────────────────────

    private void RecomputeMetricWorst()
    {
        var worst = AlertLevel.Normal;
        string? cause = null;
        double? value = null;

        // Fixed precedence on ties: cpu, mem, swap, disk (strictly-greater replaces, so first wins).
        Consider(_cpu, "cpu", ref worst, ref cause, ref value);
        Consider(_mem, "mem", ref worst, ref cause, ref value);
        Consider(_swap, "swap", ref worst, ref cause, ref value);
        foreach (var (mount, tracker) in _disks)
            Consider(tracker, "disk:" + mount, ref worst, ref cause, ref value);

        _metricWorst = worst;
        _metricCause = cause;
        _metricValue = value;
    }

    private static void Consider(
        MetricTracker? t, string name, ref AlertLevel worst, ref string? cause, ref double? value)
    {
        if (t is null || (int)t.Level <= (int)worst)
            return;
        worst = t.Level;
        cause = name;
        value = t.LastValue;
    }

    private void ApplyOverall()
    {
        var newOverall = (AlertLevel)Math.Max((int)_metricWorst, (int)_connection);

        // Connection state dominates the cause/value only when it actually outranks the metrics.
        bool connectionDominates = _connection != AlertLevel.Normal && (int)_connection >= (int)_metricWorst;
        string? cause = connectionDominates ? "connection" : _metricCause;
        double? value = connectionDominates ? null : _metricValue;

        if (newOverall == State)
            return;

        var old = State;
        State = newOverall;
        bool escalation = (int)newOverall > (int)old;

        LogTransition(old, newOverall, cause, value, escalation);
        StateChanged?.Invoke(this, new AlertStateChangedEventArgs(old, newOverall, cause, value));

        if (escalation)
            MaybeTrigger(newOverall, cause, value);
    }

    private void MaybeTrigger(AlertLevel level, string? cause, double? value)
    {
        var now = _clock();
        // Same level within its cooldown ⇒ suppress. A higher level has its own (likely-unset/expired)
        // entry, so it fires immediately — the cooldown is per level, so escalation pierces it (§6.4).
        if (_lastFired.TryGetValue(level, out var last) && now - last < AlertTuning.Cooldown)
            return;

        _lastFired[level] = now;
        _logger?.Log(LogSeverity.Info, "alert triggered", new Dictionary<string, object?>
        {
            ["server_id"] = ServerId,
            ["level"] = level.ToString(),
            ["cause"] = cause,
            ["value"] = value,
        });
        AlertTriggered?.Invoke(this, new AlertTriggeredEventArgs(level, cause, value));
    }

    private void ResetDebounce()
    {
        if (_cpu is not null) _cpu.EscalateCount = 0;
        if (_mem is not null) _mem.EscalateCount = 0;
        if (_swap is not null) _swap.EscalateCount = 0;
        foreach (var t in _disks.Values) t.EscalateCount = 0;
    }

    private void LogTransition(AlertLevel from, AlertLevel to, string? cause, double? value, bool escalation)
    {
        if (_logger is null)
            return;

        // Escalations to Warning+ are Warning; everything else is Info (§6 logging note).
        var severity = escalation && (int)to >= (int)AlertLevel.Warning
            ? LogSeverity.Warning
            : LogSeverity.Info;

        _logger.Log(severity, "alert state change", new Dictionary<string, object?>
        {
            ["server_id"] = ServerId,
            ["from"] = from.ToString(),
            ["to"] = to.ToString(),
            ["cause"] = cause,
            ["value"] = value,
        });
    }
}
