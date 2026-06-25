using System.Collections.Generic;
using VpsWatcher.App.Services;
using VpsWatcher.Core.Alerts;

namespace VpsWatcher.App.Tests;

/// <summary>
/// Phase 6c §2: the pure alert-audio ordering model — priority, FIFO ties, cap (drop lowest/oldest),
/// and consecutive-dedup. Deterministic, no threads.
/// </summary>
public sealed class AlertAudioQueueTests
{
    [Fact]
    public void Takes_highest_level_first()
    {
        var q = new AlertAudioQueue();
        q.Enqueue(AlertLevel.Caution, "c");
        q.Enqueue(AlertLevel.Critical, "x");
        q.Enqueue(AlertLevel.Warning, "w");

        Assert.Equal("x", q.TakeNext()); // Critical
        Assert.Equal("w", q.TakeNext()); // Warning
        Assert.Equal("c", q.TakeNext()); // Caution
        Assert.Null(q.TakeNext());
    }

    [Fact]
    public void Ties_are_fifo()
    {
        var q = new AlertAudioQueue();
        q.Enqueue(AlertLevel.Warning, "first");
        q.Enqueue(AlertLevel.Warning, "second");

        Assert.Equal("first", q.TakeNext());
        Assert.Equal("second", q.TakeNext());
    }

    [Fact]
    public void Caps_at_five_dropping_lowest_level_oldest()
    {
        var q = new AlertAudioQueue();
        q.Enqueue(AlertLevel.Warning, "a");
        q.Enqueue(AlertLevel.Caution, "b"); // lowest + oldest ⇒ the drop victim
        q.Enqueue(AlertLevel.Caution, "c");
        q.Enqueue(AlertLevel.Warning, "d");
        q.Enqueue(AlertLevel.Warning, "e");
        Assert.Equal(5, q.Count);

        q.Enqueue(AlertLevel.Warning, "f"); // 6th ⇒ evicts "b"
        Assert.Equal(5, q.Count);

        var drained = new List<string?>();
        for (string? s = q.TakeNext(); s is not null; s = q.TakeNext())
            drained.Add(s);

        Assert.DoesNotContain("b", drained);
        Assert.Equal(5, drained.Count);
        Assert.Equal(new[] { "a", "d", "e", "f", "c" }, drained); // Warnings (FIFO) then the surviving Caution
    }

    [Fact]
    public void Suppresses_consecutive_identical()
    {
        var q = new AlertAudioQueue();
        q.Enqueue(AlertLevel.Warning, "same");
        q.Enqueue(AlertLevel.Warning, "same");

        Assert.Equal("same", q.TakeNext()); // first plays
        Assert.Null(q.TakeNext());          // identical right after ⇒ suppressed
    }

    [Fact]
    public void Same_file_again_after_a_different_one_is_allowed()
    {
        var q = new AlertAudioQueue();
        q.Enqueue(AlertLevel.Critical, "x");
        q.Enqueue(AlertLevel.Warning, "y");
        q.Enqueue(AlertLevel.Caution, "x"); // x again, but not consecutive after it plays

        Assert.Equal("x", q.TakeNext());
        Assert.Equal("y", q.TakeNext());
        Assert.Equal("x", q.TakeNext()); // allowed (last taken was "y")
    }
}
