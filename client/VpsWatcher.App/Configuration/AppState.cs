using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace VpsWatcher.App.Configuration;

/// <summary>
/// App + UI state persisted to <c>%APPDATA%\VpsWatcher\state.json</c> (design §9.2). Phase 3b owns
/// only <see cref="AlwaysOnTop"/> and <see cref="WindowPosition"/>; keys this phase doesn't model
/// yet (<c>order</c>, <c>masterVolume</c>) are carried through verbatim via <see cref="Extra"/> so
/// a future phase's data is never clobbered. Missing keys fall back to defaults.
/// </summary>
public sealed class AppState
{
    [JsonPropertyName("alwaysOnTop")]
    public bool AlwaysOnTop { get; set; }

    [JsonPropertyName("windowPosition")]
    public WindowPosition? WindowPosition { get; set; }

    /// <summary>Round-trips any keys we don't explicitly model (forward-compat, §9.2).</summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; set; }
}

/// <summary>Window top-left in device-independent pixels (§9.2).</summary>
public sealed class WindowPosition
{
    [JsonPropertyName("x")] public double X { get; set; }
    [JsonPropertyName("y")] public double Y { get; set; }
}

/// <summary>
/// Load/save for <see cref="AppState"/>. Fail-soft on read (missing / corrupt file → defaults,
/// never throws) so a bad state.json can't stop the gadget from launching.
/// </summary>
public static class AppStateStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>Default user-local path: <c>%APPDATA%\VpsWatcher\state.json</c> (outside the repo).</summary>
    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "VpsWatcher", "state.json");

    public static AppState Load(string path)
    {
        try
        {
            if (!File.Exists(path))
                return new AppState();

            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<AppState>(json, Options) ?? new AppState();
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            // Fail-soft: a corrupt/unreadable state.json must not block startup.
            return new AppState();
        }
    }

    public static void Save(string path, AppState state)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        File.WriteAllText(path, JsonSerializer.Serialize(state, Options));
    }
}
