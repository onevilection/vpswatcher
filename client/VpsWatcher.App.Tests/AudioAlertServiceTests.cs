using System.Collections.Concurrent;
using System.IO;
using System.Threading;
using VpsWatcher.App.Services;
using VpsWatcher.Core.Alerts;

namespace VpsWatcher.App.Tests;

/// <summary>
/// Phase 6c §2: the threaded audio worker plays serially (one at a time, never overlapping), in
/// priority order, and a click interrupts the current playback. Verified through a fake player that
/// records start order, enforces single concurrency, and lets the test control completion.
/// </summary>
public sealed class AudioAlertServiceTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(3);

    private sealed class FakePlayer : IAudioPlayer
    {
        public readonly BlockingCollection<string> Started = new();
        public readonly ConcurrentBag<string> Cancelled = new();
        private readonly SemaphoreSlim _complete = new(0);
        private int _active;
        public int MaxConcurrent;

        public void Play(SoundSource source, float volume, CancellationToken ct)
        {
            int now = Interlocked.Increment(ref _active);
            lock (this) MaxConcurrent = Math.Max(MaxConcurrent, now);
            Started.Add(source.FileName);
            try
            {
                while (!_complete.Wait(10))
                    if (ct.IsCancellationRequested) { Cancelled.Add(source.FileName); return; }
            }
            finally { Interlocked.Decrement(ref _active); }
        }

        public void Complete() => _complete.Release();
        public string NextStarted() => Started.TryTake(out var s, Timeout) ? s : throw new TimeoutException("no Play started");
    }

    private sealed class StubResolver : ISoundResolver
    {
        public SoundSource Resolve(string fileName) => new(fileName, () => Stream.Null);
    }

    private static AudioAlertService NewService(FakePlayer p) =>
        new(p, new StubResolver(), () => 1f);

    [Fact]
    public void Plays_serially_in_priority_order_without_overlap()
    {
        var player = new FakePlayer();
        using var svc = NewService(player);

        svc.Enqueue(AlertLevel.Warning, "a");
        Assert.Equal("a", player.NextStarted()); // worker started "a" and is now blocked in Play

        // While "a" is sounding, queue a lower and a higher priority alert.
        svc.Enqueue(AlertLevel.Caution, "c");
        svc.Enqueue(AlertLevel.Critical, "b");

        player.Complete();                          // finish "a"
        Assert.Equal("b", player.NextStarted());    // Critical jumps ahead of Caution
        player.Complete();
        Assert.Equal("c", player.NextStarted());
        player.Complete();

        Assert.Equal(1, player.MaxConcurrent);      // never overlapped
    }

    [Fact]
    public void Click_interrupts_the_currently_playing_alert()
    {
        var player = new FakePlayer();
        using var svc = NewService(player);

        svc.Enqueue(AlertLevel.Warning, "alert");
        Assert.Equal("alert", player.NextStarted());

        svc.PlayNow(AlertLevel.Normal, "click");    // user clicked → interrupt
        Assert.Equal("click", player.NextStarted()); // click plays at once
        Assert.Contains("alert", player.Cancelled);  // the alert was cut off

        player.Complete();
    }
}
