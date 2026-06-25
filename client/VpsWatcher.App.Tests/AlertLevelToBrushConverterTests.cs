using System.Globalization;
using System.Windows.Media;
using VpsWatcher.App.Converters;
using VpsWatcher.Core.Alerts;

namespace VpsWatcher.App.Tests;

/// <summary>
/// Phase 6a §2: each <see cref="AlertLevel"/> maps to the design §6.1 colour. The brushes are frozen
/// and shared, so we assert the resolved <see cref="Color"/> (value-equal) rather than instance.
/// </summary>
public sealed class AlertLevelToBrushConverterTests
{
    private static Color Hex(string h) => (Color)ColorConverter.ConvertFromString(h);

    [Theory]
    [InlineData(AlertLevel.Normal, "#5BD17E")]
    [InlineData(AlertLevel.Caution, "#FFC107")]
    [InlineData(AlertLevel.Warning, "#FF9F45")]
    [InlineData(AlertLevel.Critical, "#FF6B6B")]
    [InlineData(AlertLevel.Disconnected, "#9AA1AA")]
    [InlineData(AlertLevel.HostKeyMismatch, "#B06BFF")]
    public void Each_level_maps_to_its_colour(AlertLevel level, string expectedHex)
    {
        var brush = AlertLevelToBrushConverter.Brush(level);
        Assert.Equal(Hex(expectedHex), brush.Color);
    }

    [Fact]
    public void Brushes_are_frozen_and_shared()
    {
        var a = AlertLevelToBrushConverter.Brush(AlertLevel.Warning);
        var b = AlertLevelToBrushConverter.Brush(AlertLevel.Warning);
        Assert.True(a.IsFrozen);
        Assert.Same(a, b); // one shared instance per level — no per-binding allocation (§13.2)
    }

    [Fact]
    public void Convert_routes_through_the_table_and_defaults_to_normal()
    {
        var conv = new AlertLevelToBrushConverter();

        var warning = (SolidColorBrush)conv.Convert(AlertLevel.Warning, typeof(Brush), null, CultureInfo.InvariantCulture);
        Assert.Equal(Hex("#FF9F45"), warning.Color);

        // Non-AlertLevel input falls back to Normal rather than throwing.
        var fallback = (SolidColorBrush)conv.Convert("nonsense", typeof(Brush), null, CultureInfo.InvariantCulture);
        Assert.Equal(Hex("#5BD17E"), fallback.Color);
    }
}
