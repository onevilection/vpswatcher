using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;
using VpsWatcher.Core.Alerts;

namespace VpsWatcher.App.Converters;

/// <summary>
/// Maps an <see cref="AlertLevel"/> to its display colour (design §6.1 / Phase 6a §2). Used as the
/// Foreground of each metric's number so the value carries its own band colour, and of the server
/// identifier for connection states (Disconnected / HostKeyMismatch).
///
/// Brushes are created once and frozen (§13.2) — the same shared instance is returned for every
/// binding, so colouring N rows costs no per-frame allocation. The colour table is exposed via
/// <see cref="Brush"/> so it can be asserted in tests without spinning up XAML.
/// </summary>
public sealed class AlertLevelToBrushConverter : IValueConverter
{
    // §6.1 palette: green / yellow / orange / red metric bands, grey for a lost connection,
    // purple for the top-priority host-key mismatch (MITM suspicion).
    private static readonly SolidColorBrush Normal = Freeze("#5BD17E");
    private static readonly SolidColorBrush Caution = Freeze("#FFC107");
    private static readonly SolidColorBrush Warning = Freeze("#FF9F45");
    private static readonly SolidColorBrush Critical = Freeze("#FF6B6B");
    private static readonly SolidColorBrush Disconnected = Freeze("#9AA1AA");
    private static readonly SolidColorBrush HostKeyMismatch = Freeze("#B06BFF");

    /// <summary>The frozen brush for a level (also the seam used by tests).</summary>
    public static SolidColorBrush Brush(AlertLevel level) => level switch
    {
        AlertLevel.Caution => Caution,
        AlertLevel.Warning => Warning,
        AlertLevel.Critical => Critical,
        AlertLevel.Disconnected => Disconnected,
        AlertLevel.HostKeyMismatch => HostKeyMismatch,
        _ => Normal,
    };

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => Brush(value is AlertLevel l ? l : AlertLevel.Normal);

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();

    private static SolidColorBrush Freeze(string hex)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        brush.Freeze();
        return brush;
    }
}
