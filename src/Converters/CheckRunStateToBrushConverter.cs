using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;
using PrMonitor.Models;

namespace PrMonitor.Converters;

/// <summary>
/// Maps a <see cref="CheckRunState"/> to the brush used for a job row in the CI checks panel.
/// With <c>ConverterParameter="Name"</c> it returns the text brush for the job name instead of
/// the status-icon brush, dimming checks that never ran.
/// Brushes are static and frozen: the panel can hold a hundred rows and is reopened often.
/// </summary>
public sealed class CheckRunStateToBrushConverter : IValueConverter
{
    // GitHub's own status colors, matching CIStateToBrushConverter.
    private static readonly SolidColorBrush Green    = Brush("#3FB950"); // success
    private static readonly SolidColorBrush Red      = Brush("#F85149"); // failure
    private static readonly SolidColorBrush Amber    = Brush("#D29922"); // running
    private static readonly SolidColorBrush Orange   = Brush("#F0883E"); // cancelled
    private static readonly SolidColorBrush Gray     = Brush("#8B949E"); // queued
    private static readonly SolidColorBrush DimGray  = Brush("#484F58"); // skipped / unknown
    private static readonly SolidColorBrush Text     = Brush("#E6EDF3"); // regular job name
    private static readonly SolidColorBrush TextDim  = Brush("#6E7681"); // job name that never ran

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not CheckRunState state)
            return DimGray;

        return parameter is "Name"
            ? state is CheckRunState.Skipped or CheckRunState.Neutral ? TextDim : Text
            : StateToColor(state);
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();

    private static SolidColorBrush StateToColor(CheckRunState state) => state switch
    {
        CheckRunState.Success   => Green,
        CheckRunState.Failure   => Red,
        CheckRunState.Cancelled => Orange,
        CheckRunState.Running   => Amber,
        CheckRunState.Queued    => Gray,
        _                       => DimGray,
    };

    private static SolidColorBrush Brush(string hex)
    {
        var color = (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(hex);
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}
