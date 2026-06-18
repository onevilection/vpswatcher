using System.IO;
using VpsWatcher.App.Configuration;

namespace VpsWatcher.App.Tests;

/// <summary>
/// Persistence tests for <c>state.json</c> (design §9.2). File I/O is exercised against a real
/// temp file (deleted per test). Forward-compat is the key contract: missing keys fall back to
/// defaults, a corrupt file never throws, and keys this phase doesn't own (order / masterVolume)
/// survive a load→save round-trip so a future phase's data isn't clobbered.
/// </summary>
public sealed class AppStateStoreTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"vpswatcher_state_{Guid.NewGuid():N}.json");

    public void Dispose()
    {
        if (File.Exists(_path)) File.Delete(_path);
    }

    [Fact]
    public void Save_then_Load_round_trips_position_and_alwaysOnTop()
    {
        var state = new AppState
        {
            AlwaysOnTop = true,
            WindowPosition = new WindowPosition { X = 1600, Y = 80 },
        };

        AppStateStore.Save(_path, state);
        var loaded = AppStateStore.Load(_path);

        Assert.True(loaded.AlwaysOnTop);
        Assert.NotNull(loaded.WindowPosition);
        Assert.Equal(1600, loaded.WindowPosition!.X);
        Assert.Equal(80, loaded.WindowPosition.Y);
    }

    [Fact]
    public void Load_missing_file_returns_defaults()
    {
        var loaded = AppStateStore.Load(_path); // file never created

        Assert.False(loaded.AlwaysOnTop);
        Assert.Null(loaded.WindowPosition);
    }

    [Fact]
    public void Load_corrupt_file_returns_defaults_without_throwing()
    {
        File.WriteAllText(_path, "{ this is not valid json ");

        var loaded = AppStateStore.Load(_path);

        Assert.False(loaded.AlwaysOnTop);
        Assert.Null(loaded.WindowPosition);
    }

    [Fact]
    public void Load_tolerates_missing_keys()
    {
        File.WriteAllText(_path, "{}");

        var loaded = AppStateStore.Load(_path);

        Assert.False(loaded.AlwaysOnTop);
        Assert.Null(loaded.WindowPosition);
    }

    [Fact]
    public void Save_preserves_unknown_keys_this_phase_does_not_own()
    {
        // A future state.json with order/masterVolume (§9.2) we don't model yet.
        File.WriteAllText(_path,
            "{\"alwaysOnTop\":false,\"order\":[\"vps-2\",\"vps-1\"],\"masterVolume\":0.8}");

        var loaded = AppStateStore.Load(_path);
        loaded.AlwaysOnTop = true;             // change only what we own
        AppStateStore.Save(_path, loaded);

        // Save must have actually rewritten the file (reload reflects our change)...
        Assert.True(AppStateStore.Load(_path).AlwaysOnTop);
        // ...while preserving keys we don't model yet (§9.2 order / masterVolume).
        var raw = File.ReadAllText(_path);
        Assert.Contains("masterVolume", raw);
        Assert.Contains("order", raw);
        Assert.Contains("vps-2", raw);
    }
}
