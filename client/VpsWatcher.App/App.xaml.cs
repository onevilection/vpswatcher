using System.ComponentModel;
using System.Windows;
using VpsWatcher.App.Configuration;
using VpsWatcher.App.Services;
using VpsWatcher.App.ViewModels;

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

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _statePath = AppStateStore.DefaultPath;
        _state = AppStateStore.Load(_statePath);

        var dispatcher = new WpfUiDispatcher(Dispatcher);
        var config = AppServerConfigLoader.Load(e.Args, out var configError);

        _mainViewModel = new MainViewModel(config, configError, dispatcher)
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
        base.OnExit(e);
    }
}
