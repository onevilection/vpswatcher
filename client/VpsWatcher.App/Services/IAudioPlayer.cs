using System.IO;

namespace VpsWatcher.App.Services;

/// <summary>
/// A playable sound: its file name (the extension picks the decoder) and a factory that opens a fresh
/// stream each time — a user-override file on disk, or the app-bundled resource. Resolved by
/// <see cref="ISoundResolver"/>.
/// </summary>
public sealed record SoundSource(string FileName, Func<Stream> OpenStream);

/// <summary>Resolves a sound file name to a <see cref="SoundSource"/> (user override → bundled).</summary>
public interface ISoundResolver
{
    SoundSource Resolve(string fileName);
}

/// <summary>
/// The actual playback engine (design §7). Abstracted so the queue/priority/interrupt behaviour can be
/// unit-tested against a fake that records what was played, in what order, without real audio output.
/// </summary>
public interface IAudioPlayer
{
    /// <summary>Plays <paramref name="source"/> to completion at <paramref name="volume"/> (0..1),
    /// blocking the calling (worker) thread. Returns early if <paramref name="ct"/> is cancelled
    /// (used to interrupt for a click). Must not throw on a bad/missing file — it logs and returns.</summary>
    void Play(SoundSource source, float volume, CancellationToken ct);
}
