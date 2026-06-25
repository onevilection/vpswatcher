using CommunityToolkit.Mvvm.ComponentModel;
using VpsWatcher.Core.Alerts;

namespace VpsWatcher.App.ViewModels;

/// <summary>
/// One mount point's gauge (design §5.3). Updated in place across 1Hz samples so only the changed
/// percentage notifies (§13.2) — the panel's disk rows are not rebuilt every second.
/// </summary>
public sealed partial class DiskGaugeViewModel : ObservableObject
{
    public DiskGaugeViewModel(string mount) => Mount = mount;

    /// <summary>Mount path, e.g. "/" or "/var/www". Identity for in-place reconciliation; set once.</summary>
    public string Mount { get; }

    /// <summary>The root filesystem is the panel's representative disk (shown on the server's main
    /// row); other mounts expand onto sub-rows below it (Phase 6a layout).</summary>
    public bool IsRoot => Mount == "/";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(UsedText))]
    [NotifyPropertyChangedFor(nameof(PctText))]
    private double _usedPct;

    [ObservableProperty]
    private double _usedGb;

    [ObservableProperty]
    private double _totalGb;

    /// <summary>Per-mount alert band (§6) — drives this row's number colour. Set by the owning
    /// <see cref="ServerViewModel"/> from its state machine after each sample.</summary>
    [ObservableProperty]
    private AlertLevel _level = AlertLevel.Normal;

    /// <summary>Compact percentage for the sub-row, e.g. "67%" (no decimals, fits the 200px panel).</summary>
    public string PctText => $"{UsedPct:0}%";

    /// <summary>Full display text (used by the detail/tooltip), e.g. "48.1% (24.0 / 50.0 GB)".</summary>
    public string UsedText => $"{UsedPct:0.0}% ({UsedGb:0.0} / {TotalGb:0.0} GB)";
}
