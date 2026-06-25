using System.IO;
using System.Windows;
using VpsWatcher.App.Configuration;

namespace VpsWatcher.App.Services;

/// <summary>
/// Resolves a sound file name to a stream factory (design §7 user-overridable): (1) a WAV the user
/// dropped in <c>%APPDATA%\VpsWatcher\assets\voice\{file}</c>, else (2) the app-bundled resource
/// compiled at <c>pack://…/Assets/voice/{file}</c>. NAudio reads from the stream, so embedded
/// resources are served via <see cref="Application.GetResourceStream(Uri)"/>.
/// </summary>
public sealed class SoundResolver : ISoundResolver
{
    private readonly string _userVoiceDir;
    private readonly Func<string, bool> _fileExists;

    public SoundResolver(string? userVoiceDir = null, Func<string, bool>? fileExists = null)
    {
        _userVoiceDir = userVoiceDir ?? AppearanceStore.DefaultUserVoiceDir;
        _fileExists = fileExists ?? File.Exists;
    }

    public SoundSource Resolve(string fileName)
    {
        var userPath = Path.Combine(_userVoiceDir, fileName);
        if (_fileExists(userPath))
            return new SoundSource(fileName, () => File.OpenRead(userPath));

        var uri = new Uri($"pack://application:,,,/VpsWatcher.App;component/Assets/voice/{fileName}", UriKind.Absolute);
        return new SoundSource(fileName, () =>
        {
            var info = Application.GetResourceStream(uri)
                ?? throw new FileNotFoundException($"bundled sound not found: {fileName}");
            return info.Stream;
        });
    }
}
