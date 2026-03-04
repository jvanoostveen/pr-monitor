using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;
using PrMonitor.Models;

namespace PrMonitor.Converters;

/// <summary>
/// Maps a <see cref="CIState"/> to a <see cref="SolidColorBrush"/>
/// suitable for use as a status-circle fill color.
/// </summary>
public sealed class CIStateToBrushConverter : IValueConverter
{
    // GitHub's own status colors
    private static readonly SolidColorBrush Green   = Brush("#3FB950"); // success
    private static readonly SolidColorBrush Red     = Brush("#F85149"); // failure
    private static readonly SolidColorBrush Amber   = Brush("#D29922"); // pending / in-progress
    private static readonly SolidColorBrush Orange  = Brush("#F0883E"); // error
    private static readonly SolidColorBrush DimGray = Brush("#484F58"); // unknown

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is CIState state ? StateToColor(state) : DimGray;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();

    private static SolidColorBrush StateToColor(CIState state) => state switch
    {
        CIState.Success => Green,
        CIState.Failure => Red,
        CIState.Pending => Amber,
        CIState.Error   => Orange,
        _               => DimGray,
    };

    private static SolidColorBrush Brush(string hex)
    {
        var color = (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(hex);
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}
