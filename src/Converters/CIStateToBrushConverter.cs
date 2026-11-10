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
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is CIState state ? StateToColor(state) : StatusBrushes.DimGray;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();

    /// <summary>The frozen status brush for <paramref name="state"/>, for use outside bindings.</summary>
    internal static SolidColorBrush StateToColor(CIState state) => state switch
    {
        CIState.Success => StatusBrushes.Green,
        CIState.Failure => StatusBrushes.Red,
        CIState.Pending => StatusBrushes.Amber,
        CIState.Error   => StatusBrushes.Orange,
        _               => StatusBrushes.DimGray,
    };
}
