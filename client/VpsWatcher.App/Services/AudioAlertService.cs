using VpsWatcher.Core.Alerts;
using VpsWatcher.Core.Logging;

namespace VpsWatcher.App.Services;

/// <summary>
/// Serial alert-audio worker (design §7 / §6.4). A single background thread drains an
/// <see cref="AlertAudioQueue"/> through an injected <see cref="IAudioPlayer"/>: one sound at a time
/// (never overlapping), highest level first, with the cap / dedup the queue enforces. A currently
/// playing alert is never interrupted by another alert — only by a click (<see cref="PlayNow"/>),
/// which cancels the current playback and jumps the queue.
///
/// The player is injected so tests verify <em>which</em> sounds were played and <em>in what order</em>
/// (and that one finishes before the next starts) without real audio.
/// </summary>
public sealed class AudioAlertService : IAlertAudio
{
    private readonly IAudioPlayer _player;
    private readonly ISoundResolver _resolver;
    private readonly Func<float> _volume;
    private readonly IAppLogger? _logger;

    private readonly object _gate = new();
    private readonly AlertAudioQueue _queue = new();
    private string? _interruptFile;
    private CancellationTokenSource? _currentCts;
    private readonly SemaphoreSlim _signal = new(0);
    private readonly Thread _worker;
    private volatile bool _disposed;

    public AudioAlertService(
        IAudioPlayer player, ISoundResolver resolver, Func<float> volume, IAppLogger? logger = null)
    {
        _player = player ?? throw new ArgumentNullException(nameof(player));
        _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
        _volume = volume ?? throw new ArgumentNullException(nameof(volume));
        _logger = logger;

        _worker = new Thread(Loop) { IsBackground = true, Name = "VpsWatcher.Audio" };
        _worker.Start();
    }

    public void Enqueue(AlertLevel level, string fileName)
    {
        if (_disposed || string.IsNullOrEmpty(fileName))
            return;
        lock (_gate)
            _queue.Enqueue(level, fileName);
        _signal.Release();
    }

    public void PlayNow(AlertLevel level, string fileName)
    {
        if (_disposed || string.IsNullOrEmpty(fileName))
            return;
        lock (_gate)
        {
            _interruptFile = fileName;
            _currentCts?.Cancel(); // stop whatever is sounding so the click is heard at once
        }
        _signal.Release();
    }

    private void Loop()
    {
        while (!_disposed)
        {
            _signal.Wait();
            if (_disposed)
                return;
            DrainOnce();
        }
    }

    private void DrainOnce()
    {
        while (!_disposed)
        {
            string? file;
            CancellationTokenSource cts;
            lock (_gate)
            {
                if (_interruptFile is { } itr)
                {
                    _interruptFile = null;
                    file = itr;
                    _queue.NoteTaken(itr); // so an identical alert right after the click is deduped
                }
                else
                {
                    file = _queue.TakeNext();
                }

                if (file is null)
                    return;

                cts = _currentCts = new CancellationTokenSource();
            }

            try
            {
                _player.Play(_resolver.Resolve(file), _volume(), cts.Token);
            }
            catch (Exception ex)
            {
                _logger?.Log(LogSeverity.Warning, "audio resolve/play failed", new Dictionary<string, object?>
                {
                    ["reason"] = ex.GetType().Name,
                    ["file"] = file,
                });
            }
            finally
            {
                lock (_gate)
                {
                    if (ReferenceEquals(_currentCts, cts))
                        _currentCts = null;
                    cts.Dispose();
                }
            }
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        lock (_gate)
            _currentCts?.Cancel();
        _signal.Release();
        _worker.Join(TimeSpan.FromSeconds(2));
        _signal.Dispose();
    }
}
