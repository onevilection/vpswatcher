using System.Collections.Generic;
using System.Linq;
using VpsWatcher.App.Configuration;
using VpsWatcher.App.Services;
using VpsWatcher.App.ViewModels;
using VpsWatcher.Core.Alerts;
using VpsWatcher.Core.Configuration;
using VpsWatcher.Core.Schema;
using VpsWatcher.Core.Ssh;

namespace VpsWatcher.App.Tests;

/// <summary>
/// Phase 6c §4: MainViewModel routes alert escalations into the audio queue and click-to-speak into
/// immediate playback. Uses a recording fake audio + the internal ServerViewModel-list ctor.
/// </summary>
public sealed class MainViewModelAudioTests
{
    private sealed class FakeAudio : IAlertAudio
    {
        public readonly List<(AlertLevel Level, string File)> Enqueued = new();
        public readonly List<(AlertLevel Level, string File)> PlayedNow = new();
        public void Enqueue(AlertLevel level, string fileName) => Enqueued.Add((level, fileName));
        public void PlayNow(AlertLevel level, string fileName) => PlayedNow.Add((level, fileName));
        public void Dispose() { }
    }

    private sealed class ManualScheduler : IRecoveryScheduler
    {
        public void Schedule(TimeSpan delay, Action onElapsed) { }
        public void Cancel() { }
    }

    private static ServerViewModel Server(string id) => new(id, id, new SynchronousDispatcher(), new ServerThresholds
    {
        Cpu = new double[] { 70, 85, 95 },
        Mem = new double[] { 75, 88, 95 },
        Disk = new double[] { 80, 90, 95 },
        Swap = new double[] { 25, 50, 80 },
    });

    private static Sample Sample(double cpu, double mem, double swap, params (string m, double p)[] disks) => new()
    {
        Id = "vps", CpuPct = cpu, Mem = new Mem { UsedPct = mem }, Swap = new Swap { UsedPct = swap },
        Disk = disks.Select(d => new DiskEntry { Mount = d.m, UsedPct = d.p }).ToArray(),
    };

    private static void Feed(ServerViewModel vm, Sample s)
    {
        for (int i = 0; i < 3; i++)
            vm.HandleMetrics(null, new MetricsReceivedEventArgs(s));
    }

    private static MainViewModel Vm(FakeAudio audio, params ServerViewModel[] servers) =>
        new(servers, appearance: new AppearanceConfig(), images: null, recovery: new ManualScheduler(),
            logger: null, audio: audio);

    [Fact]
    public void Alert_escalation_enqueues_the_matching_sound()
    {
        var audio = new FakeAudio();
        var s = Server("a");
        using var vm = Vm(audio, s);

        Feed(s, Sample(cpu: 96, mem: 1, swap: 0, ("/", 1))); // cpu → Critical, fires AlertTriggered

        Assert.Contains((AlertLevel.Critical, "cpu_critical.wav"), audio.Enqueued);
    }

    [Fact]
    public void Recovering_to_normal_enqueues_the_recovery_voice()
    {
        var audio = new FakeAudio();
        var s = Server("a");
        using var vm = Vm(audio, s);

        Feed(s, Sample(cpu: 96, mem: 1, swap: 0, ("/", 1))); // → Critical
        // One low sample demotes immediately (no debounce on the way down) → worst-of back to Normal.
        s.HandleMetrics(null, new MetricsReceivedEventArgs(Sample(cpu: 1, mem: 1, swap: 0, ("/", 1))));

        Assert.Equal(AlertLevel.Normal, vm.WorstState);
        Assert.Contains((AlertLevel.Normal, "recovery.wav"), audio.Enqueued);
    }

    [Fact]
    public void Click_while_normal_plays_all_clear_and_flashes_recovery()
    {
        var audio = new FakeAudio();
        using var vm = Vm(audio, Server("a")); // never fed ⇒ Normal

        vm.CharacterClickCommand.Execute(null);

        Assert.Contains((AlertLevel.Normal, "all_ok.wav"), audio.PlayedNow);
        Assert.Equal(CharacterMood.Recovery, vm.CurrentMood); // transient yorokobi (ManualScheduler holds it)
    }

    [Fact]
    public void Click_while_critical_plays_the_cause_sound_at_once()
    {
        var audio = new FakeAudio();
        var s = Server("a");
        using var vm = Vm(audio, s);
        Feed(s, Sample(cpu: 97, mem: 1, swap: 0, ("/", 1))); // cpu Critical
        audio.PlayedNow.Clear(); // ignore anything before the click

        vm.CharacterClickCommand.Execute(null);

        Assert.Contains((AlertLevel.Critical, "cpu_critical.wav"), audio.PlayedNow);
    }
}
