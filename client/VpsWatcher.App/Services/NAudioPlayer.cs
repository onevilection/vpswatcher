using System.IO;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using VpsWatcher.Core.Logging;

namespace VpsWatcher.App.Services;

/// <summary>
/// NAudio-backed <see cref="IAudioPlayer"/> (design §7). Plays one sound to completion on the calling
/// (worker) thread, applying the master volume, and stops early when cancelled (click interrupt).
/// Every reader / output device is created per call and disposed in a finally (no leaks). A
/// bad/missing/corrupt file is logged (reason only, no path → no secrets §4) and swallowed, never
/// crashing the app.
/// </summary>
public sealed class NAudioPlayer : IAudioPlayer
{
    private readonly IAppLogger? _logger;

    public NAudioPlayer(IAppLogger? logger = null) => _logger = logger;

    public void Play(SoundSource source, float volume, CancellationToken ct)
    {
        Stream? stream = null;
        WaveStream? reader = null;
        WaveOutEvent? output = null;
        try
        {
            stream = source.OpenStream();
            reader = source.FileName.EndsWith(".mp3", StringComparison.OrdinalIgnoreCase)
                ? new Mp3FileReader(stream)
                : new WaveFileReader(stream);

            var sample = new VolumeSampleProvider(reader.ToSampleProvider())
            {
                Volume = Math.Clamp(volume, 0f, 1f),
            };

            output = new WaveOutEvent();
            output.Init(sample);
            output.Play();

            while (output.PlaybackState == PlaybackState.Playing)
            {
                if (ct.IsCancellationRequested)
                {
                    output.Stop();
                    break;
                }
                Thread.Sleep(20);
            }
        }
        catch (Exception ex)
        {
            // Fail-soft: skip this sound. Reason + file name only (the bundled/override name is not a
            // secret; we never log the resolved %APPDATA% path).
            _logger?.Log(LogSeverity.Warning, "audio playback failed", new Dictionary<string, object?>
            {
                ["reason"] = ex.GetType().Name,
                ["file"] = source.FileName,
            });
        }
        finally
        {
            output?.Dispose();
            reader?.Dispose();
            stream?.Dispose();
        }
    }
}
