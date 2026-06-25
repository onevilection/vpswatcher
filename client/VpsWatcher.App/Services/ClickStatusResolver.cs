using VpsWatcher.App.ViewModels;
using VpsWatcher.Core.Alerts;

namespace VpsWatcher.App.Services;

/// <summary>
/// Works out what the character should "say" when clicked (design §4, Phase 6c), from the worst-of
/// state and the per-metric levels/values already on the <see cref="ServerViewModel"/>s.
/// <list type="bullet">
///   <item>Normal ⇒ all-clear (cause null).</item>
///   <item>Disconnected / HostKeyMismatch ⇒ that state's connection sound (cause null).</item>
///   <item>A metric band (Caution/Warning/Critical) ⇒ the metric that is AT the worst level with the
///         highest usage % (across all servers): cpu / mem / disk / swap.</item>
/// </list>
/// Pure: it only reads bound properties, so it is unit-testable with hand-fed ViewModels.
/// </summary>
public static class ClickStatusResolver
{
    /// <summary>The (level, cause) to voice on click. <c>cause</c> is a normalised metric key
    /// (cpu/mem/disk/swap) for metric bands, or null for Normal / connection states.</summary>
    public static (AlertLevel Level, string? Cause) Resolve(
        IEnumerable<ServerViewModel> servers, AlertLevel worst)
    {
        if (worst is AlertLevel.Normal or AlertLevel.Disconnected or AlertLevel.HostKeyMismatch)
            return (worst, null);

        // Metric band: pick the highest-% metric sitting at exactly the worst level.
        string? cause = null;
        double bestPct = double.NegativeInfinity;

        void Consider(AlertLevel level, double pct, string name)
        {
            if (level == worst && pct > bestPct)
            {
                bestPct = pct;
                cause = name;
            }
        }

        foreach (var s in servers)
        {
            Consider(s.CpuLevel, s.CpuPct ?? 0, "cpu");
            Consider(s.MemLevel, s.MemPct, "mem");
            Consider(s.SwapLevel, s.SwapPct, "swap");
            foreach (var disk in s.Disks)
                Consider(disk.Level, disk.UsedPct, "disk");
        }

        // Fallback: worst is a metric band but (timing) no metric matched — pick a safe cause.
        return (worst, cause ?? "cpu");
    }
}
