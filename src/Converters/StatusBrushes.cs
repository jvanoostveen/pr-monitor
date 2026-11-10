using System.Windows.Media;

namespace PrMonitor.Converters;

/// <summary>
/// GitHub's own status colours, as static frozen brushes shared by the status converters:
/// rows are rebuilt often and a brush per binding would be retained per row.
/// </summary>
internal static class StatusBrushes
{
    public static readonly SolidColorBrush Green   = Frozen("#3FB950"); // success
    public static readonly SolidColorBrush Red     = Frozen("#F85149"); // failure
    public static readonly SolidColorBrush Amber   = Frozen("#D29922"); // pending / running
    public static readonly SolidColorBrush Orange  = Frozen("#F0883E"); // error / cancelled
    public static readonly SolidColorBrush DimGray = Frozen("#484F58"); // unknown / skipped

    public static SolidColorBrush Frozen(string hex)
    {
        var color = (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(hex);
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}
