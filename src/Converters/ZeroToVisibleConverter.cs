using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace PrMonitor.Converters;

/// <summary>
/// Returns <see cref="Visibility.Visible"/> when the bound integer is 0,
/// <see cref="Visibility.Collapsed"/> otherwise.
/// Used for "empty state" messages.
/// </summary>
public sealed class ZeroToVisibleConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is int n && n == 0 ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
