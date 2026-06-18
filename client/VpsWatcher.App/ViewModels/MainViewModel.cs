using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using VpsWatcher.App.Services;
using VpsWatcher.Core.Configuration;
using VpsWatcher.Core.Ssh;

namespace VpsWatcher.App.ViewModels;

/// <summary>
/// Root window ViewModel. Phase 3a holds a single <see cref="ServerViewModel"/>, but keeps the
/// <see cref="ObservableCollection{T}"/> container so N-server support (ItemsControl) drops in at
/// Phase 3b without reshaping the View. Owns the server's <see cref="SshConnectionService"/>,
/// wires its events to the ViewModel, and starts it.
/// </summary>
public sealed partial class MainViewModel : ObservableObject, IDisposable
{
    private readonly SshConnectionService? _service;
    private readonly ServerViewModel? _vm;

    /// <summary>All server panels (one in Phase 3a).</summary>
    public ObservableCollection<ServerViewModel> Servers { get; } = new();

    /// <summary>The single server panel (Phase 3a convenience binding); null when unconfigured.</summary>
    public ServerViewModel? Server => _vm;

    /// <summary>Set when there is no server to show (config missing / connect failed). Drives the empty state.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasServer))]
    private string? _statusMessage;

    public bool HasServer => _vm is not null;

    /// <summary>
    /// Always-on-top toggle (design §5.2.1). Bound two-way to the window's <c>Topmost</c> and to the
    /// pin toggle; persisted to state.json (§9.2). Window-level setting (one window in Phase 3b).
    /// </summary>
    [ObservableProperty]
    private bool _alwaysOnTop;

    public MainViewModel(ServerConfig? config, string? configError, IUiDispatcher dispatcher)
    {
        if (config is null)
        {
            StatusMessage = configError ?? "接続先が未設定です。";
            return;
        }

        try
        {
            _vm = new ServerViewModel(config.Id, config.Label, dispatcher);
            Servers.Add(_vm);

            // SshConnectionService validates the config and loads the key in its ctor — surface
            // any failure as the empty state rather than crashing startup.
            _service = new SshConnectionService(config);
            _service.MetricsReceived += _vm.HandleMetrics;
            _service.StateChanged += _vm.HandleStateChanged;
            _service.Start();
        }
        catch (Exception ex)
        {
            _service = null;
            _vm = null;
            Servers.Clear();
            StatusMessage = $"接続の初期化に失敗しました: {ex.Message}";
        }
    }

    public void Dispose()
    {
        if (_service is not null && _vm is not null)
        {
            _service.MetricsReceived -= _vm.HandleMetrics;
            _service.StateChanged -= _vm.HandleStateChanged;
        }
        _service?.Dispose();
    }
}
