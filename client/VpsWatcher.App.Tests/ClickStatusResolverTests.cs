using System.Linq;
using VpsWatcher.App.Services;
using VpsWatcher.App.ViewModels;
using VpsWatcher.Core.Alerts;
using VpsWatcher.Core.Configuration;
using VpsWatcher.Core.Schema;
using VpsWatcher.Core.Ssh;

namespace VpsWatcher.App.Tests;

/// <summary>
/// Phase 6c §4: click-to-speak resolves the worst-of state to (level, cause) — Normal / connection
/// states carry no cause; a metric band picks the highest-% metric sitting at the worst level.
/// </summary>
public sealed class ClickStatusResolverTests
{
    private static ServerThresholds Std() => new()
    {
        Cpu = new double[] { 70, 85, 95 },
        Mem = new double[] { 75, 88, 95 },
        Disk = new double[] { 80, 90, 95 },
        Swap = new double[] { 25, 50, 80 },
    };

    private static ServerViewModel Server(string id) => new(id, id, new SynchronousDispatcher(), Std());

    private static Sample Sample(double cpu, double mem, double swap, params (string m, double p)[] disks) => new()
    {
        Id = "vps", CpuPct = cpu, Mem = new Mem { UsedPct = mem }, Swap = new Swap { UsedPct = swap },
        Disk = disks.Select(d => new DiskEntry { Mount = d.m, UsedPct = d.p }).ToArray(),
    };

    private static void Feed(ServerViewModel vm, Sample s)
    {
        for (int i = 0; i < 3; i++) // pass promotion debounce
            vm.HandleMetrics(null, new MetricsReceivedEventArgs(s));
    }

    private static void SetState(ServerViewModel vm, ConnectionState st) =>
        vm.HandleStateChanged(null, new ConnectionStateChangedEventArgs(ConnectionState.Connecting, st, null));

    [Fact]
    public void Normal_has_no_cause()
    {
        var s = Server("a");
        var (level, cause) = ClickStatusResolver.Resolve(new[] { s }, AlertLevel.Normal);
        Assert.Equal(AlertLevel.Normal, level);
        Assert.Null(cause);
    }

    [Theory]
    [InlineData(ConnectionState.Disconnected, AlertLevel.Disconnected)]
    [InlineData(ConnectionState.HostKeyMismatch, AlertLevel.HostKeyMismatch)]
    public void Connection_states_have_no_cause(ConnectionState st, AlertLevel worst)
    {
        var s = Server("a");
        SetState(s, st);
        var (level, cause) = ClickStatusResolver.Resolve(new[] { s }, worst);
        Assert.Equal(worst, level);
        Assert.Null(cause);
    }

    [Fact]
    public void Metric_band_picks_highest_pct_metric_at_worst_level()
    {
        var s = Server("a");
        // cpu 97 and disk 96 are both Critical; cpu has the higher %.
        Feed(s, Sample(cpu: 97, mem: 1, swap: 0, ("/", 96)));
        Assert.Equal(AlertLevel.Critical, s.AlertState);

        var (level, cause) = ClickStatusResolver.Resolve(new[] { s }, AlertLevel.Critical);
        Assert.Equal(AlertLevel.Critical, level);
        Assert.Equal("cpu", cause);
    }

    [Fact]
    public void Disk_wins_when_it_is_the_highest_at_the_worst_level()
    {
        var s = Server("a");
        Feed(s, Sample(cpu: 96, mem: 1, swap: 0, ("/", 99))); // both Critical; disk higher %
        var (_, cause) = ClickStatusResolver.Resolve(new[] { s }, AlertLevel.Critical);
        Assert.Equal("disk", cause);
    }

    [Fact]
    public void Considers_all_servers()
    {
        var a = Server("a");
        var b = Server("b");
        Feed(a, Sample(cpu: 96, mem: 1, swap: 0, ("/", 1)));   // a: cpu Critical 96
        Feed(b, Sample(cpu: 1, mem: 98, swap: 0, ("/", 1)));   // b: mem Critical 98 (higher)

        var (_, cause) = ClickStatusResolver.Resolve(new[] { a, b }, AlertLevel.Critical);
        Assert.Equal("mem", cause); // highest % across both
    }
}
