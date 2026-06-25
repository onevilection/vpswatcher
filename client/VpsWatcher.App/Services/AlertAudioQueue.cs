using VpsWatcher.Core.Alerts;

namespace VpsWatcher.App.Services;

/// <summary>
/// The pure ordering model behind the alert audio worker (design §7 / §6.4). No threads, no NAudio —
/// just the policy, so it can be unit-tested deterministically:
/// <list type="bullet">
///   <item><b>Priority</b>: the next sound to play is the highest <see cref="AlertLevel"/> waiting
///         (ties broken oldest-first / FIFO).</item>
///   <item><b>Cap</b>: at most <see cref="MaxPending"/> sounds wait; on overflow the lowest-level,
///         oldest item is dropped (so the gadget never falls behind babbling).</item>
///   <item><b>Dedup</b>: a sound identical to the one just taken is suppressed (skipped) — a backstop
///         on top of the state machine's cooldown (§6.4).</item>
/// </list>
/// The currently-playing sound is owned by the worker, not this queue, so it is never interrupted by
/// reordering (alert-vs-alert). Click-to-speak interruption is handled separately by the service.
/// </summary>
public sealed class AlertAudioQueue
{
    public const int MaxPending = 5;

    private readonly List<(int Seq, AlertLevel Level, string File)> _items = new();
    private int _seq;
    private string? _lastTaken;

    public int Count => _items.Count;

    /// <summary>Queues a sound, enforcing the cap by dropping the lowest-level, oldest waiter on overflow.</summary>
    public void Enqueue(AlertLevel level, string file)
    {
        _items.Add((_seq++, level, file));
        while (_items.Count > MaxPending)
        {
            int victim = 0;
            for (int i = 1; i < _items.Count; i++)
                if (_items[i].Level < _items[victim].Level ||
                    (_items[i].Level == _items[victim].Level && _items[i].Seq < _items[victim].Seq))
                    victim = i;
            _items.RemoveAt(victim);
        }
    }

    /// <summary>Removes and returns the next file to play (highest level, oldest on ties), skipping any
    /// that duplicate the previous taken sound. Returns null when nothing remains to play.</summary>
    public string? TakeNext()
    {
        while (_items.Count > 0)
        {
            int best = 0;
            for (int i = 1; i < _items.Count; i++)
                if (_items[i].Level > _items[best].Level ||
                    (_items[i].Level == _items[best].Level && _items[i].Seq < _items[best].Seq))
                    best = i;

            var file = _items[best].File;
            _items.RemoveAt(best);

            if (file == _lastTaken)
                continue; // consecutive-identical ⇒ suppress, try the next

            _lastTaken = file;
            return file;
        }
        return null;
    }

    /// <summary>Records a file played out-of-band (e.g. a click) so the next dedup compares against it.</summary>
    public void NoteTaken(string file) => _lastTaken = file;
}
