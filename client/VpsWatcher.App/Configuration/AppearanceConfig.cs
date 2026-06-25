using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using VpsWatcher.Core.Alerts;
using VpsWatcher.Core.Logging;

namespace VpsWatcher.App.Configuration;

/// <summary>
/// The character's expression as a function of the overall (worst-of) state (design §8). Mirrors
/// <see cref="AlertLevel"/> plus the transient <see cref="Recovery"/> shown the moment the worst
/// state drops back to Normal. These names are the keys in <c>appearance.json</c>'s
/// <c>expressions</c> map, so they are part of the user-facing contract.
/// </summary>
public enum CharacterMood
{
    Normal,
    Caution,
    Warning,
    Critical,
    Disconnected,
    HostKeyMismatch,
    Recovery,
}

/// <summary>
/// User-editable look settings persisted to <c>%APPDATA%\VpsWatcher\appearance.json</c> (design §8 /
/// §9.2). Holds the state→PNG expression map and the panel background opacity. Kept WPF-free (pure
/// POCO + pure resolution) so it is unit-testable without a Dispatcher; the WPF glue (decoding /
/// freezing the images, building the brush) lives in the View layer.
///
/// Forward-compat: voice mapping (§7, Phase 6c) will land as another key on this same file, so
/// unknown keys are round-tripped via <see cref="Extra"/> rather than dropped.
/// </summary>
public sealed class AppearanceConfig
{
    /// <summary>Panel background opacity 0..1 (1 = opaque). Absent / invalid → <see cref="DefaultOpacity"/>.</summary>
    [JsonPropertyName("background_opacity")]
    public double? BackgroundOpacity { get; set; }

    /// <summary>Mood name (<see cref="CharacterMood"/>) → PNG file name. Missing entries fall back to
    /// the bundled default for that mood.</summary>
    [JsonPropertyName("expressions")]
    public Dictionary<string, string>? Expressions { get; set; }

    /// <summary>Master playback volume 0..1 (§9.2). Absent / invalid → <see cref="DefaultMasterVolume"/>.</summary>
    [JsonPropertyName("master_volume")]
    public double? MasterVolume { get; set; }

    /// <summary>Alert sounds (§7): level name → (cause metric → WAV file). The special cause key
    /// <c>_default</c> is used when no per-metric sound is configured. Missing entries fall back to the
    /// bundled <see cref="DefaultSounds"/>.</summary>
    [JsonPropertyName("sounds")]
    public Dictionary<string, Dictionary<string, string>>? Sounds { get; set; }

    /// <summary>Click-to-speak settings (§4): the all-clear sound + expression played when the user
    /// clicks the character while everything is Normal.</summary>
    [JsonPropertyName("click")]
    public ClickConfig? Click { get; set; }

    /// <summary>Round-trips keys this phase doesn't model yet so they survive a save.</summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; set; }

    /// <summary>Default panel opacity (matches the 6a hard-coded <c>#E6…</c> = 230/255 ≈ 0.9).</summary>
    public const double DefaultOpacity = 0.9;

    /// <summary>Below this background opacity the panel is too see-through for white text, so the
    /// normal text (e.g. the server identifier) switches to a dark colour (Phase 6b-fix §3). At or
    /// above it, text stays light. Metric numbers keep their level colour regardless.</summary>
    public const double DarkTextOpacityThreshold = 0.3;

    /// <summary>True when the (effective) background is faint enough to need dark text.</summary>
    public bool UseDarkText() => EffectiveOpacity() < DarkTextOpacityThreshold;

    /// <summary>Bundled default expression PNGs per mood (§8). The file names match the repo's
    /// <c>assets/char/</c> assets compiled into the app's resources.</summary>
    public static readonly IReadOnlyDictionary<CharacterMood, string> DefaultExpressions =
        new Dictionary<CharacterMood, string>
        {
            [CharacterMood.Normal] = "mei-hutuu.png",
            [CharacterMood.Caution] = "mei-nayami.png",
            [CharacterMood.Warning] = "mei-odoroki.png",
            [CharacterMood.Critical] = "mei-obie.png",
            [CharacterMood.Disconnected] = "mei-konnrann.png",
            [CharacterMood.HostKeyMismatch] = "mei-ikari.png",
            [CharacterMood.Recovery] = "mei-yorokobi.png",
        };

    /// <summary>Maps the overall alert level to its mood (Recovery is driven separately, on the
    /// non-Normal → Normal transition).</summary>
    public static CharacterMood MoodFor(AlertLevel level) => level switch
    {
        AlertLevel.Caution => CharacterMood.Caution,
        AlertLevel.Warning => CharacterMood.Warning,
        AlertLevel.Critical => CharacterMood.Critical,
        AlertLevel.Disconnected => CharacterMood.Disconnected,
        AlertLevel.HostKeyMismatch => CharacterMood.HostKeyMismatch,
        _ => CharacterMood.Normal,
    };

    /// <summary>Effective opacity in [0,1]: out-of-range values are clamped; null / NaN fall back to
    /// <see cref="DefaultOpacity"/> (Phase 6b §5).</summary>
    public double EffectiveOpacity()
    {
        if (BackgroundOpacity is not { } v || double.IsNaN(v))
            return DefaultOpacity;
        return Math.Clamp(v, 0.0, 1.0);
    }

    /// <summary>Effective opacity as an 8-bit alpha for the ARGB background colour.</summary>
    public byte EffectiveAlphaByte() => (byte)Math.Round(EffectiveOpacity() * 255.0);

    /// <summary>The configured (or default) PNG file name for a mood. Blank / missing → bundled default.</summary>
    public string FileNameFor(CharacterMood mood)
    {
        if (Expressions is not null
            && Expressions.TryGetValue(mood.ToString(), out var name)
            && !string.IsNullOrWhiteSpace(name))
        {
            return name.Trim();
        }
        return DefaultExpressions[mood];
    }

    // ───────────────────────── audio (§7) ─────────────────────────

    /// <summary>Default master volume when unset/invalid (§9.2).</summary>
    public const double DefaultMasterVolume = 0.8;

    /// <summary>Last-resort sound when neither a per-cause nor a level <c>_default</c> exists.</summary>
    public const string FallbackSound = "critical_default.wav";

    /// <summary>Bundled default sound map (§7.2): level → (cause → WAV). Metric bands carry one WAV per
    /// metric; connection / recovery states carry only a <c>_default</c>.</summary>
    public static readonly IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> DefaultSounds =
        new Dictionary<string, IReadOnlyDictionary<string, string>>
        {
            ["Caution"] = new Dictionary<string, string>
            { ["cpu"] = "cpu_caution.wav", ["mem"] = "mem_caution.wav", ["disk"] = "disk_caution.wav", ["swap"] = "swap_caution.wav" },
            ["Warning"] = new Dictionary<string, string>
            { ["cpu"] = "cpu_warning.wav", ["mem"] = "mem_warning.wav", ["disk"] = "disk_warning.wav", ["swap"] = "swap_warning.wav" },
            ["Critical"] = new Dictionary<string, string>
            { ["cpu"] = "cpu_critical.wav", ["mem"] = "mem_critical.wav", ["disk"] = "disk_critical.wav", ["swap"] = "swap_critical.wav", ["_default"] = "critical_default.wav" },
            ["Disconnected"] = new Dictionary<string, string> { ["_default"] = "disconnected.wav" },
            ["HostKeyMismatch"] = new Dictionary<string, string> { ["_default"] = "security_alert.wav" },
            ["Recovery"] = new Dictionary<string, string> { ["_default"] = "recovery.wav" },
        };

    /// <summary>Master volume in [0,1]: out-of-range clamped; null / NaN → <see cref="DefaultMasterVolume"/>.</summary>
    public float EffectiveMasterVolume()
    {
        if (MasterVolume is not { } v || double.IsNaN(v))
            return (float)DefaultMasterVolume;
        return (float)Math.Clamp(v, 0.0, 1.0);
    }

    /// <summary>Normalises an AlertStateMachine cause to a sound cause key: <c>disk:/mnt</c> → <c>disk</c>;
    /// <c>cpu/mem/swap</c> unchanged; <c>connection</c> / null / anything else → null (use the level's
    /// <c>_default</c>).</summary>
    public static string? NormalizeCause(string? cause)
    {
        if (string.IsNullOrEmpty(cause))
            return null;
        if (cause.StartsWith("disk", StringComparison.Ordinal))
            return "disk";
        return cause is "cpu" or "mem" or "swap" ? cause : null;
    }

    /// <summary>
    /// The WAV file for an alert: configured <c>sounds[level][cause]</c> → configured
    /// <c>sounds[level][_default]</c> → bundled default for that (level, cause) → bundled level
    /// <c>_default</c> → <see cref="FallbackSound"/>. <paramref name="cause"/> is a normalised metric
    /// key (cpu/mem/disk/swap) or null for connection/recovery states.
    /// </summary>
    public string SoundFileFor(AlertLevel level, string? cause)
    {
        var levelKey = level.ToString();

        // 1) user-configured map for this level
        if (Sounds is not null && Sounds.TryGetValue(levelKey, out var configured) && configured is not null)
        {
            if (cause is not null && configured.TryGetValue(cause, out var byCause) && !string.IsNullOrWhiteSpace(byCause))
                return byCause.Trim();
            if (configured.TryGetValue("_default", out var def) && !string.IsNullOrWhiteSpace(def))
                return def.Trim();
        }

        // 2) bundled defaults for this level
        if (DefaultSounds.TryGetValue(levelKey, out var defaults))
        {
            if (cause is not null && defaults.TryGetValue(cause, out var byCause))
                return byCause;
            if (defaults.TryGetValue("_default", out var def))
                return def;
        }

        // 3) last resort
        return FallbackSound;
    }

    /// <summary>All-clear sound for a click while Normal (§4). Blank / missing → bundled default.</summary>
    public string ClickOkSound()
        => Click?.OkSound is { } s && !string.IsNullOrWhiteSpace(s) ? s.Trim() : "all_ok.wav";

    /// <summary>The gentle recovery voice played when everything returns to Normal (§6.4). Driven by
    /// the worst-of transition, not an AlertTriggered, so it has its own accessor: configured
    /// <c>sounds["Recovery"]["_default"]</c> → bundled <c>recovery.wav</c>.</summary>
    public string RecoverySound()
    {
        if (Sounds is not null && Sounds.TryGetValue("Recovery", out var m) && m is not null
            && m.TryGetValue("_default", out var f) && !string.IsNullOrWhiteSpace(f))
            return f.Trim();
        return DefaultSounds["Recovery"]["_default"];
    }
}

/// <summary>Click-to-speak settings (§4, Phase 6c).</summary>
public sealed class ClickConfig
{
    /// <summary>Sound played when the user clicks the character while everything is Normal.</summary>
    [JsonPropertyName("ok_sound")] public string? OkSound { get; set; }

    /// <summary>Expression shown for the all-clear click (defaults to the Recovery portrait).</summary>
    [JsonPropertyName("ok_expression")] public string? OkExpression { get; set; }
}

/// <summary>
/// Load/save for <see cref="AppearanceConfig"/>. Fail-soft on read (missing / corrupt → defaults,
/// never throws) so a bad appearance.json can't stop the gadget from launching (Phase 6b §4).
/// </summary>
public static class AppearanceStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>Default path: <c>%APPDATA%\VpsWatcher\appearance.json</c> (outside the repo).</summary>
    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "VpsWatcher", "appearance.json");

    /// <summary>User PNG override directory: <c>%APPDATA%\VpsWatcher\assets\char</c> — a PNG dropped
    /// here (matching a configured/default file name) replaces the bundled one (§8 user-overridable).</summary>
    public static string DefaultUserCharDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "VpsWatcher", "assets", "char");

    /// <summary>User WAV override directory: <c>%APPDATA%\VpsWatcher\assets\voice</c> — a WAV dropped
    /// here (matching a configured/default file name) replaces the bundled one (§7 user-overridable).</summary>
    public static string DefaultUserVoiceDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "VpsWatcher", "assets", "voice");

    public static AppearanceConfig Load(string path, IAppLogger? logger = null)
    {
        try
        {
            if (!File.Exists(path))
                return new AppearanceConfig();

            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<AppearanceConfig>(json, Options) ?? new AppearanceConfig();
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            // Fail-soft: fall back to bundled defaults. Reason only — no secrets / paths (§4).
            logger?.Log(LogSeverity.Warning, "appearance.json unreadable; using defaults",
                new Dictionary<string, object?> { ["reason"] = ex.GetType().Name });
            return new AppearanceConfig();
        }
    }
}
