using System.Collections.Generic;
using VpsWatcher.App.ViewModels;
using VpsWatcher.Core.Alerts;
using VpsWatcher.Core.Configuration;
using VpsWatcher.Core.Schema;
using VpsWatcher.Core.Ssh;

namespace VpsWatcher.App.Tests;

/// <summary>
/// Phase 6a: the ServerViewModel mirrors each metric's alert band (cpu / mem / swap / per-disk) from
/// its <see cref="AlertStateMachine"/> onto bindable per-metric levels, which the compact View uses
/// to colour each number (§6). These run on a synchronous dispatcher (UI-thread stand-in).
/// </summary>
public sealed class ServerViewModelLevelTests
{
    private static ServerThresholds Std() => new()
    {
        Cpu = new double[] { 70, 85, 95 },
        Mem = new double[] { 75, 88, 95 },
        Disk = new double[] { 80, 90, 95 },
        Swap = new double[] { 25, 50, 80 },
    };

    private static Sample Sample(double cpu, double mem, double swap, params (string mount, double pct)[] disks)
        => new()
        {
            Id = "vps-1",
            CpuPct = cpu,
            Mem = new Mem { UsedPct = mem },
            Swap = new Swap { UsedPct = swap },
            Disk = disks.Select(d => new DiskEntry { Mount = d.mount, UsedPct = d.pct }).ToArray(),
        };

    private static ServerViewModel NewVm() =>
        new("vps-1", "A", new SynchronousDispatcher(), Std());

    private static void Feed(ServerViewModel vm, Sample s) =>
        vm.HandleMetrics(null, new MetricsReceivedEventArgs(s));

    [Fact]
    public void Per_metric_levels_start_normal()
    {
        var vm = NewVm();
        Assert.Equal(AlertLevel.Normal, vm.CpuLevel);
        Assert.Equal(AlertLevel.Normal, vm.MemLevel);
        Assert.Equal(AlertLevel.Normal, vm.SwapLevel);
        Assert.Equal(AlertLevel.Normal, vm.RootDiskLevel);
    }

    [Fact]
    public void CpuLevel_reflects_state_machine_after_debounce()
    {
        var vm = NewVm();
        // cpu 86 (>85 warning entry), everything else quiet. Promotion needs 3 consecutive samples.
        for (int i = 0; i < 3; i++)
            Feed(vm, Sample(cpu: 86, mem: 1, swap: 0, ("/", 1)));

        Assert.Equal(AlertLevel.Warning, vm.CpuLevel);
        // Other metrics stay Normal — the colours are independent per metric.
        Assert.Equal(AlertLevel.Normal, vm.MemLevel);
        Assert.Equal(AlertLevel.Normal, vm.SwapLevel);
    }

    [Fact]
    public void RootDisk_pct_and_level_track_the_root_mount()
    {
        var vm = NewVm();
        for (int i = 0; i < 3; i++)
            Feed(vm, Sample(cpu: 1, mem: 1, swap: 0, ("/", 91), ("/var", 5)));

        Assert.Equal(91, vm.RootDiskPct);
        Assert.Equal("91%", vm.RootDiskPctText);
        Assert.Equal(AlertLevel.Warning, vm.RootDiskLevel); // 91 > 90 warning entry
    }

    [Fact]
    public void Non_root_disk_gauge_gets_its_own_level()
    {
        var vm = NewVm();
        for (int i = 0; i < 3; i++)
            Feed(vm, Sample(cpu: 1, mem: 1, swap: 0, ("/", 5), ("/var", 96)));

        var varDisk = vm.Disks.Single(d => d.Mount == "/var");
        Assert.False(varDisk.IsRoot);
        Assert.Equal(AlertLevel.Critical, varDisk.Level); // 96 > 95 critical entry
        Assert.Equal("96%", varDisk.PctText);
    }

    [Fact]
    public void CpuLevel_change_raises_property_changed()
    {
        // §13.2 partial redraw: a band transition notifies CpuLevel exactly so the View re-colours
        // only that number, not the whole panel.
        var vm = NewVm();

        var changed = new List<string?>();
        vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        for (int i = 0; i < 3; i++)
            Feed(vm, Sample(cpu: 86, mem: 1, swap: 0, ("/", 1)));

        Assert.Contains(nameof(ServerViewModel.CpuLevel), changed);
    }

    [Theory]
    [InlineData("Example Server 1", "Exam")]
    [InlineData("vps-1", "vps-")]
    [InlineData("AB", "AB")]
    public void ShortId_is_first_four_chars(string label, string expected)
    {
        var vm = new ServerViewModel("id-x", label, new SynchronousDispatcher());
        Assert.Equal(expected, vm.ShortId);
    }

    [Fact]
    public void ShortId_falls_back_to_id_when_label_blank()
    {
        var vm = new ServerViewModel("vps-9", null, new SynchronousDispatcher());
        Assert.Equal("vps-", vm.ShortId);
    }
}
