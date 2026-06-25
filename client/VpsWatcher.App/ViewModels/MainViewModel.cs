using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VpsWatcher.App.Configuration;
using VpsWatcher.App.Services;
using VpsWatcher.Core.Alerts;
using VpsWatcher.Core.Configuration;
using VpsWatcher.Core.Logging;
using VpsWatcher.Core.Ssh;

namespace VpsWatcher.App.ViewModels;

/// <summary>
/// Root window ViewModel (design §5.3). Holds N <see cref="ServerViewModel"/>s in
/// <see cref="Servers"/> (rendered by the View's ItemsControl) and owns one
/// <see cref="SshConnectionService"/> per server, wired to its ViewModel and started independently.
///
/// Fail-soft (§5.4/§5.4.1): each server connects, reconnects and verifies its host key on its own —
/// one server's drop / HostKeyMismatch never affects the others. If a server's connection fails to
/// even initialise (e.g. missing key file), it is skipped with the detail sent to Trace; the
/// remaining servers still run. Only when NO server can be shown does the empty-state
/// <see cref="StatusMessage"/> appear, and it never carries secrets or paths (MEDIUM 1).
/// </summary>
public sealed partial class MainViewModel : ObservableObject, IDisposable
{
    // One (service, vm) pair per running server, so Dispose can unwire + tear them all down.
    private readonly List<(SshConnectionService Service, ServerViewModel Vm)> _connections = new();

    /// <summary>All server panels, in servers.json array order (design §5.3).</summary>
    public ObservableCollection<ServerViewModel> Servers { get; } = new();

    /// <summary>Set when there is no server to show (none configured / all failed). Drives the empty state.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasServer))]
    private string? _statusMessage;

    public bool HasServer => Servers.Count > 0;

    /// <summary>
    /// Always-on-top toggle (design §5.2.1). Bound two-way to the window's <c>Topmost</c> and to the
    /// pin toggle; persisted to state.json (§9.2). Window-level setting (one window).
    /// </summary>
    [ObservableProperty]
    private bool _alwaysOnTop;

    // ───────────────────────── character / appearance (§8) ─────────────────────────

    /// <summary>How long the transient recovery (yorokobi) portrait stays before reverting to Normal.</summary>
    private static readonly TimeSpan RecoveryDuration = TimeSpan.FromSeconds(4);

    private readonly ICharacterImageSource? _images;
    private readonly IRecoveryScheduler? _recovery;
    private readonly IAlertAudio? _audio;
    private AppearanceConfig _appearance = new();

    /// <summary>Worst-of overall state across all servers — the single "mood" of the one character
    /// (§8: one Mei expresses the worst server). Recomputed when any server's AlertState changes.</summary>
    [ObservableProperty]
    private AlertLevel _worstState = AlertLevel.Normal;

    /// <summary>Current character mood (drives the portrait). Includes the transient Recovery shown
    /// right after recovering to Normal (§8).</summary>
    [ObservableProperty]
    private CharacterMood _currentMood = CharacterMood.Normal;

    /// <summary>The (cached, frozen) portrait for <see cref="CurrentMood"/> — bound by the View's Image.</summary>
    [ObservableProperty]
    private ImageSource? _characterImage;

    /// <summary>Panel background brush; its alpha comes from appearance.json's background_opacity (§5).</summary>
    [ObservableProperty]
    private Brush? _backgroundBrush;

    /// <summary>Brush for the panel's normal text (e.g. the server identifier): light on a solid
    /// background, dark when the background is faint (background_opacity &lt; 0.3) so it stays
    /// readable (Phase 6b-fix §3). Metric numbers keep their own level colour.</summary>
    [ObservableProperty]
    private Brush? _primaryTextBrush;

    private readonly IAppLogger? _logger;

    public MainViewModel(
        IReadOnlyList<ServerConfig> configs, string? configError, IUiDispatcher dispatcher,
        IAppLogger? logger = null,
        AppearanceConfig? appearance = null,
        ICharacterImageSource? images = null,
        IRecoveryScheduler? recovery = null,
        IAlertAudio? audio = null)
    {
        _logger = logger;
        _images = images;
        _recovery = recovery;
        _audio = audio;
        _appearance = appearance ?? new AppearanceConfig();
        ApplyInitialAppearance(_appearance);

        if (configs is null || configs.Count == 0)
        {
            StatusMessage = configError ?? "接続先が未設定です。";
            return;
        }

        var failedIds = new List<string>();
        foreach (var config in configs)
        {
            try
            {
                var vm = new ServerViewModel(config.Id, config.Label, dispatcher, config.Thresholds, _logger);

                // SshConnectionService validates the config and resolves the key path in its ctor —
                // surface any per-server failure by skipping just that server (below), never by
                // crashing startup or taking the other servers down with it.
                var service = new SshConnectionService(config, _logger);
                service.MetricsReceived += vm.HandleMetrics;
                service.StateChanged += vm.HandleStateChanged;

                _connections.Add((service, vm));
                Servers.Add(vm);
                // Drive the one shared character mood off each server's overall state (§8 worst-of),
                // and its alert escalations into the voice (§7).
                vm.PropertyChanged += OnServerPropertyChanged;
                vm.AlertTriggered += OnServerAlertTriggered;
                service.Start();
            }
            catch (Exception ex)
            {
                // Never surface the raw exception text on screen OR to Trace. SSH.NET's PrivateKeyFile /
                // connect exceptions (and FileNotFoundException.FileName) embed the private-key path in
                // ex.ToString(); the empty-state message is painted on the transparent gadget, and Trace
                // is forwarded to any listener (DebugView / ETW). So Trace gets only the (non-secret)
                // server id + exception TYPE name — never {ex} (security review HIGH / MEDIUM 1 / §4).
                System.Diagnostics.Trace.TraceError(
                    $"connection init failed for server '{config.Id}': {ex.GetType().Name}");
                // Persistent, secret-safe record (§6 Error): server id + exception TYPE/stack only,
                // never ex.Message (which can embed the key path) — the logger enforces that (§4).
                _logger?.Log(LogSeverity.Error, "connection init failed",
                    new Dictionary<string, object?> { ["server_id"] = config.Id }, ex);
                failedIds.Add(config.Id);
            }
        }

        // Non-secret: the count + ids of the servers actually shown (§6 Info).
        if (Servers.Count > 0)
        {
            _logger?.Log(LogSeverity.Info, "servers loaded", new Dictionary<string, object?>
            {
                ["count"] = Servers.Count,
                ["server_ids"] = string.Join(",", Servers.Select(s => s.Id)),
            });
        }

        // Empty state only when nothing could be shown. Partial failures stay in Trace so a single
        // bad entry doesn't push a banner over the servers that are working. Ids aren't secret.
        if (Servers.Count == 0)
        {
            StatusMessage = failedIds.Count > 0
                ? $"接続の初期化に失敗しました（{string.Join(", ", failedIds)}）。詳細はデバッグ出力を参照してください。"
                : (configError ?? "接続先が未設定です。");
        }

        RecomputeWorstState(); // seed the initial mood from the (Connecting) servers
    }

    /// <summary>
    /// Test-only seam: builds the character/appearance wiring over a set of already-constructed
    /// <see cref="ServerViewModel"/>s WITHOUT any SSH connection, so the worst-of mood / recovery
    /// logic (§8) can be driven deterministically and network-free.
    /// </summary>
    internal MainViewModel(
        IReadOnlyList<ServerViewModel> servers,
        AppearanceConfig? appearance = null,
        ICharacterImageSource? images = null,
        IRecoveryScheduler? recovery = null,
        IAppLogger? logger = null,
        IAlertAudio? audio = null)
    {
        _logger = logger;
        _images = images;
        _recovery = recovery;
        _audio = audio;
        _appearance = appearance ?? new AppearanceConfig();
        ApplyInitialAppearance(_appearance);

        foreach (var vm in servers)
        {
            Servers.Add(vm);
            vm.PropertyChanged += OnServerPropertyChanged;
            vm.AlertTriggered += OnServerAlertTriggered;
        }
        RecomputeWorstState();
    }

    // ───────────────────────── worst-of mood / recovery (§8) ─────────────────────────

    /// <summary>Background opacity → brush (§5) + the initial Normal portrait. Shared by both ctors.</summary>
    private void ApplyInitialAppearance(AppearanceConfig appearance)
    {
        // RGB fixed (1E2228); alpha from the clamped/defaulted opacity.
        var brush = new SolidColorBrush(Color.FromArgb(appearance.EffectiveAlphaByte(), 0x1E, 0x22, 0x28));
        brush.Freeze();
        BackgroundBrush = brush;

        // Dark text on a faint panel, light text otherwise (§3).
        var textBrush = new SolidColorBrush(appearance.UseDarkText()
            ? Color.FromRgb(0x1A, 0x1D, 0x21)   // near-black, readable over a see-through panel
            : Color.FromRgb(0xF5, 0xF7, 0xFA));  // the existing light text colour
        textBrush.Freeze();
        PrimaryTextBrush = textBrush;

        // Initial portrait (Normal). Subsequent changes flow through OnCurrentMoodChanged.
        CharacterImage = _images?.ImageFor(CharacterMood.Normal);
    }

    private void OnServerPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        // Only the overall AlertState feeds the character — ignore the per-metric churn (§13.2).
        if (e.PropertyName == nameof(ServerViewModel.AlertState))
            RecomputeWorstState();
    }

    /// <summary>Worst-of across all servers (AlertLevel int order = priority, so plain Max). Assigning
    /// an unchanged value is a no-op, so the mood transition only runs on a real change.</summary>
    private void RecomputeWorstState()
    {
        var worst = AlertLevel.Normal;
        foreach (var s in Servers)
            if ((int)s.AlertState > (int)worst)
                worst = s.AlertState;
        WorstState = worst;
    }

    partial void OnWorstStateChanged(AlertLevel oldValue, AlertLevel newValue)
    {
        if (newValue == AlertLevel.Normal && oldValue != AlertLevel.Normal)
        {
            // Recovered to all-clear: the gentle recovery voice (§6.4, low priority so a fresh alert
            // outranks it) + the transient yorokobi portrait, then revert to Normal after a beat.
            _audio?.Enqueue(AlertLevel.Normal, _appearance.RecoverySound());
            ShowRecoveryThenNormal();
        }
        else
        {
            // Any other transition cancels a pending recovery-revert and shows the new mood at once.
            _recovery?.Cancel();
            CurrentMood = AppearanceConfig.MoodFor(newValue);
        }
    }

    /// <summary>Shows the transient recovery (yorokobi) portrait, reverting to Normal after a beat
    /// (unless something re-escalated meanwhile). Used by both the recovery transition and an
    /// all-clear click.</summary>
    private void ShowRecoveryThenNormal()
    {
        CurrentMood = CharacterMood.Recovery;
        if (_recovery is not null)
            _recovery.Schedule(RecoveryDuration, () =>
            {
                if (WorstState == AlertLevel.Normal)
                    CurrentMood = CharacterMood.Normal;
            });
        else
            CurrentMood = CharacterMood.Normal; // no scheduler (tests) ⇒ skip the transient hold
    }

    partial void OnCurrentMoodChanged(CharacterMood value)
        => CharacterImage = _images?.ImageFor(value);

    // ───────────────────────── audio (§7) ─────────────────────────

    /// <summary>An escalation sounded a voice (§6.4/§7): pick the WAV for (level, cause) and queue it.</summary>
    private void OnServerAlertTriggered(object? sender, AlertTriggeredEventArgs e)
    {
        var file = _appearance.SoundFileFor(e.Level, AppearanceConfig.NormalizeCause(e.Cause));
        _audio?.Enqueue(e.Level, file);
    }

    /// <summary>
    /// Click-to-speak (§4): the user clicked the character, so voice the current worst-of status at
    /// once (interrupting any alert). All-clear also flashes the recovery portrait.
    /// </summary>
    [RelayCommand]
    private void CharacterClick()
    {
        var (level, cause) = ClickStatusResolver.Resolve(Servers, WorstState);

        if (level == AlertLevel.Normal)
        {
            _audio?.PlayNow(AlertLevel.Normal, _appearance.ClickOkSound());
            ShowRecoveryThenNormal();
            return;
        }

        _audio?.PlayNow(level, _appearance.SoundFileFor(level, cause));
    }

    /// <summary>How long shutdown waits for the reconnect loops to wind down before giving up.</summary>
    private static readonly TimeSpan ShutdownGrace = TimeSpan.FromSeconds(3);

    public void Dispose()
    {
        // Unwire first so a late event can't touch a ViewModel mid-teardown.
        foreach (var (service, vm) in _connections)
        {
            service.MetricsReceived -= vm.HandleMetrics;
            service.StateChanged -= vm.HandleStateChanged;
            vm.PropertyChanged -= OnServerPropertyChanged;
            vm.AlertTriggered -= OnServerAlertTriggered;
        }
        _recovery?.Cancel();
        _audio?.Dispose();

        // Bundle the cancellation: StopAsync cancels each loop's token, then we await them together
        // (bounded) so no SSH read loop / connection is left running past window close — instead of
        // only signalling cancel and racing process exit. Loops use ConfigureAwait(false) and never
        // need the UI thread, so blocking here can't deadlock. Best-effort: a hung loop must not
        // wedge shutdown, hence the grace timeout.
        var stops = _connections.Select(c => c.Service.StopAsync()).ToArray();
        try { Task.WaitAll(stops, ShutdownGrace); }
        catch { /* swallow on shutdown: we still Dispose below regardless */ }

        foreach (var (service, _) in _connections)
            service.Dispose();
        _connections.Clear();
    }
}
