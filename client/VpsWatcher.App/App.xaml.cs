using System.ComponentModel;
using System.IO;
using System.Windows;
using VpsWatcher.App.Configuration;
using VpsWatcher.App.Services;
using VpsWatcher.App.ViewModels;
using VpsWatcher.Core.Logging;

namespace VpsWatcher.App;

/// <summary>
/// Application entry point. Loads the connection target (args/env or user-local servers.json) and
/// the persisted UI state (state.json, §9.2), wires the <see cref="MainViewModel"/> to the gadget
/// window, restores window position + always-on-top, and persists those on change / exit.
/// Real connection values are never read from the repo (CLAUDE.md).
/// </summary>
public partial class App : Application
{
    private MainViewModel? _mainViewModel;
    private MainWindow? _window;
    private AppState _state = new();
    private string _statePath = AppStateStore.DefaultPath;
    private IAppLogger? _logger;

    /// <summary>Where the NDJSON logs live: %APPDATA%\VpsWatcher\log (outside the repo, §3/秘密情報).</summary>
    private static string LogDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "VpsWatcher", "log");

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Logging first, so everything below (config load, connections) can be recorded (§5/§6).
        bool debugMode = LaunchOptions.IsDebugMode(e.Args);
        _logger = AppLogger.CreateFile("VpsWatcher", debugMode, LogDirectory);
        _logger.Log(LogSeverity.Info, "VpsWatcher started",
            new Dictionary<string, object?> { ["debug_mode"] = debugMode ? "on" : "off" });

        _statePath = AppStateStore.DefaultPath;
        _state = AppStateStore.Load(_statePath);

        var dispatcher = new WpfUiDispatcher(Dispatcher);
        var configs = AppServerConfigLoader.Load(e.Args, out var configError);

        // Appearance (§8): user-editable expression map + background opacity. Fail-soft to bundled
        // defaults. Portraits are decoded + frozen up front so state changes only swap the cached
        // ImageSource (§13.2). The recovery (yorokobi) revert runs on a one-shot UI-thread timer.
        var appearance = AppearanceStore.Load(AppearanceStore.DefaultPath, _logger);
        var images = new CharacterImageProvider(appearance, logger: _logger);
        images.PreloadAll();
        var recovery = new DispatcherRecoveryScheduler(Dispatcher);

        // Audio (§7): serial alert voice + click-to-speak. Volume is read live so an edited
        // appearance.json master_volume could take effect without rebuilding the service.
        var audio = new AudioAlertService(
            new NAudioPlayer(_logger), new SoundResolver(), () => appearance.EffectiveMasterVolume(), _logger);

        _mainViewModel = new MainViewModel(
            configs, configError, dispatcher, _logger, appearance, images, recovery, audio)
        {
            AlwaysOnTop = _state.AlwaysOnTop, // restore (§5.2.1)
        };
        _mainViewModel.PropertyChanged += OnViewModelPropertyChanged;

        _window = new MainWindow { DataContext = _mainViewModel };
        _window.WindowMoved += OnWindowMoved;
        RestorePosition(_window, _state.WindowPosition);

        _window.Show();
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.AlwaysOnTop) && _mainViewModel is not null)
        {
            _state.AlwaysOnTop = _mainViewModel.AlwaysOnTop;
            Persist();
        }
    }

    private void OnWindowMoved(double x, double y)
    {
        _state.WindowPosition = new WindowPosition { X = x, Y = y };
        Persist();
    }

    /// <summary>
    /// Restores a saved top-left only if it still sits within the current virtual screen (monitors
    /// can change between runs) — otherwise we leave the default centred placement so the gadget
    /// can't be stranded off-screen.
    /// </summary>
    private static void RestorePosition(Window window, WindowPosition? pos)
    {
        if (pos is null)
            return;

        double minX = SystemParameters.VirtualScreenLeft;
        double minY = SystemParameters.VirtualScreenTop;
        double maxX = minX + SystemParameters.VirtualScreenWidth;
        double maxY = minY + SystemParameters.VirtualScreenHeight;

        // Require a visible margin of the top-left corner to remain on a screen.
        const double margin = 48;
        bool onScreen = pos.X >= minX && pos.Y >= minY
            && pos.X <= maxX - margin && pos.Y <= maxY - margin;

        if (!onScreen)
            return;

        window.WindowStartupLocation = WindowStartupLocation.Manual;
        window.Left = pos.X;
        window.Top = pos.Y;
    }

    private void Persist()
    {
        try { AppStateStore.Save(_statePath, _state); }
        catch { /* best-effort: never let a failed state write disrupt the running gadget */ }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        // Final flush: capture the latest window placement + toggle before shutdown.
        if (_window is not null)
            _state.WindowPosition = new WindowPosition { X = _window.Left, Y = _window.Top };
        if (_mainViewModel is not null)
            _state.AlwaysOnTop = _mainViewModel.AlwaysOnTop;
        Persist();

        _mainViewModel?.Dispose();

        // Log exit, then dispose the logger last so the line is flushed to the file (§6 Info).
        _logger?.Log(LogSeverity.Info, "VpsWatcher exiting");
        _logger?.Dispose();

        base.OnExit(e);
    }
}
