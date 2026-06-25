using VpsWatcher.Core.Alerts;

namespace VpsWatcher.App.Services;

/// <summary>
/// The gadget's voice (design §7). Alerts are queued and played serially (never overlapping);
/// click-to-speak plays immediately, interrupting whatever is currently sounding.
/// </summary>
public interface IAlertAudio : IDisposable
{
    /// <summary>Queues an alert sound (serial, priority-ordered, deduped, capped). Non-blocking.</summary>
    void Enqueue(AlertLevel level, string fileName);

    /// <summary>Plays a sound at once, interrupting the current playback (user clicked the character).</summary>
    void PlayNow(AlertLevel level, string fileName);
}
