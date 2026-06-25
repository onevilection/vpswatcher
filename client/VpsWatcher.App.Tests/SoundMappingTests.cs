using VpsWatcher.App.Configuration;
using VpsWatcher.Core.Alerts;

namespace VpsWatcher.App.Tests;

/// <summary>
/// Phase 6c §3: alert→WAV selection, cause normalisation, master volume, and the click sound — all
/// pure on <see cref="AppearanceConfig"/>.
/// </summary>
public sealed class SoundMappingTests
{
    // ───────────────────────── selection (defaults) ─────────────────────────

    [Theory]
    [InlineData(AlertLevel.Caution, "cpu", "cpu_caution.wav")]
    [InlineData(AlertLevel.Warning, "mem", "mem_warning.wav")]
    [InlineData(AlertLevel.Critical, "disk", "disk_critical.wav")]
    [InlineData(AlertLevel.Critical, "swap", "swap_critical.wav")]
    public void Selects_per_cause_default(AlertLevel level, string cause, string expected)
        => Assert.Equal(expected, new AppearanceConfig().SoundFileFor(level, cause));

    [Fact]
    public void Falls_back_to_level_default_when_no_cause_sound()
    {
        var cfg = new AppearanceConfig();
        Assert.Equal("critical_default.wav", cfg.SoundFileFor(AlertLevel.Critical, null));
        Assert.Equal("disconnected.wav", cfg.SoundFileFor(AlertLevel.Disconnected, "cpu")); // cause ignored
        Assert.Equal("security_alert.wav", cfg.SoundFileFor(AlertLevel.HostKeyMismatch, null));
    }

    [Fact]
    public void Caution_with_unknown_cause_uses_last_resort_fallback()
        => Assert.Equal("critical_default.wav", new AppearanceConfig().SoundFileFor(AlertLevel.Caution, null));

    [Fact]
    public void Configured_sound_overrides_default()
    {
        var cfg = new AppearanceConfig
        {
            Sounds = new() { ["Critical"] = new() { ["cpu"] = "my_cpu.wav" } },
        };
        Assert.Equal("my_cpu.wav", cfg.SoundFileFor(AlertLevel.Critical, "cpu"));
        // unspecified cause for that level still falls back to the bundled default
        Assert.Equal("disk_critical.wav", cfg.SoundFileFor(AlertLevel.Critical, "disk"));
    }

    // ───────────────────────── cause normalisation ─────────────────────────

    [Theory]
    [InlineData("cpu", "cpu")]
    [InlineData("mem", "mem")]
    [InlineData("swap", "swap")]
    [InlineData("disk:/", "disk")]
    [InlineData("disk:/var/www", "disk")]
    [InlineData("connection", null)]
    [InlineData(null, null)]
    public void Normalises_cause(string? raw, string? expected)
        => Assert.Equal(expected, AppearanceConfig.NormalizeCause(raw));

    // ───────────────────────── master volume ─────────────────────────

    [Theory]
    [InlineData(0.5, 0.5f)]
    [InlineData(0.0, 0.0f)]
    [InlineData(1.0, 1.0f)]
    [InlineData(1.5, 1.0f)]   // clamp
    [InlineData(-0.2, 0.0f)]  // clamp
    public void Master_volume_clamps(double input, float expected)
        => Assert.Equal(expected, new AppearanceConfig { MasterVolume = input }.EffectiveMasterVolume());

    [Fact]
    public void Master_volume_defaults_when_unset_or_nan()
    {
        Assert.Equal(0.8f, new AppearanceConfig().EffectiveMasterVolume());
        Assert.Equal(0.8f, new AppearanceConfig { MasterVolume = double.NaN }.EffectiveMasterVolume());
    }

    // ───────────────────────── click sound ─────────────────────────

    [Fact]
    public void Click_ok_sound_default_and_override()
    {
        Assert.Equal("all_ok.wav", new AppearanceConfig().ClickOkSound());
        Assert.Equal("yay.wav",
            new AppearanceConfig { Click = new ClickConfig { OkSound = "yay.wav" } }.ClickOkSound());
    }

    [Fact]
    public void Recovery_sound_default_and_override()
    {
        Assert.Equal("recovery.wav", new AppearanceConfig().RecoverySound());
        Assert.Equal("phew.wav",
            new AppearanceConfig { Sounds = new() { ["Recovery"] = new() { ["_default"] = "phew.wav" } } }.RecoverySound());
    }
}
