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
    private static readonly SolidColorBrush Gray    = StatusBrushes.Frozen("#8B949E"); // queued
    private static readonly SolidColorBrush Text    = StatusBrushes.Frozen("#E6EDF3"); // regular job name
    private static readonly SolidColorBrush TextDim = StatusBrushes.Frozen("#6E7681"); // job name that never ran

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not CheckRunState state)
            return StatusBrushes.DimGray;

        return parameter is "Name"
            ? state is CheckRunState.Skipped or CheckRunState.Neutral ? TextDim : Text
            : StateToColor(state);
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();

    private static SolidColorBrush StateToColor(CheckRunState state) => state switch
    {
        CheckRunState.Success   => StatusBrushes.Green,
        CheckRunState.Failure   => StatusBrushes.Red,
        CheckRunState.Cancelled => StatusBrushes.Orange,
        CheckRunState.Running   => StatusBrushes.Amber,
        CheckRunState.Queued    => Gray,
        _                       => StatusBrushes.DimGray,
    };
}
