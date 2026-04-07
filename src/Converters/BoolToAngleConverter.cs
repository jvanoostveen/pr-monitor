using System.Globalization;
using System.Windows.Data;

namespace PrMonitor.Converters;

/// <summary>
/// Converts a bool to a rotation angle for the section chevron:
/// <c>true</c> (expanded) → 90°, <c>false</c> (collapsed) → 0°.
/// </summary>
public sealed class BoolToAngleConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is true ? 90.0 : 0.0;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
