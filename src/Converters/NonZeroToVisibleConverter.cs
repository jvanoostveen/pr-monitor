using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace PrMonitor.Converters;

/// <summary>
/// Returns <see cref="Visibility.Visible"/> when the bound integer is greater than 0,
/// <see cref="Visibility.Collapsed"/> when it is 0.
/// Used to hide a section entirely when it has no items.
/// </summary>
public sealed class NonZeroToVisibleConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is int n && n > 0 ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
