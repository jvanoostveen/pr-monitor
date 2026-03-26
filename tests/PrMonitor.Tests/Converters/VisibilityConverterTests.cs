using System.Globalization;
using System.Windows;
using PrMonitor.Converters;
using Xunit;

namespace PrMonitor.Tests.Converters;

public class VisibilityConverterTests
{
    private readonly ZeroToVisibleConverter _zeroConverter = new();
    private readonly NonZeroToVisibleConverter _nonZeroConverter = new();

    [Fact]
    public void ZeroToVisible_Zero_ReturnsVisible() =>
        Assert.Equal(Visibility.Visible, _zeroConverter.Convert(0, typeof(Visibility), null!, CultureInfo.InvariantCulture));

    [Theory]
    [InlineData(1)]
    [InlineData(5)]
    [InlineData(-1)]
    public void ZeroToVisible_NonZero_ReturnsCollapsed(int value) =>
        Assert.Equal(Visibility.Collapsed, _zeroConverter.Convert(value, typeof(Visibility), null!, CultureInfo.InvariantCulture));

    [Fact]
    public void ZeroToVisible_NonIntInput_ReturnsCollapsed() =>
        Assert.Equal(Visibility.Collapsed, _zeroConverter.Convert("hello", typeof(Visibility), null!, CultureInfo.InvariantCulture));

    [Theory]
    [InlineData(1)]
    [InlineData(10)]
    public void NonZeroToVisible_PositiveInt_ReturnsVisible(int value) =>
        Assert.Equal(Visibility.Visible, _nonZeroConverter.Convert(value, typeof(Visibility), null!, CultureInfo.InvariantCulture));

    [Fact]
    public void NonZeroToVisible_Zero_ReturnsCollapsed() =>
        Assert.Equal(Visibility.Collapsed, _nonZeroConverter.Convert(0, typeof(Visibility), null!, CultureInfo.InvariantCulture));

    [Fact]
    public void NonZeroToVisible_NonIntInput_ReturnsCollapsed() =>
        Assert.Equal(Visibility.Collapsed, _nonZeroConverter.Convert("hello", typeof(Visibility), null!, CultureInfo.InvariantCulture));

    [Fact]
    public void ZeroToVisible_ConvertBack_Throws() =>
        Assert.Throws<NotSupportedException>(() =>
            _zeroConverter.ConvertBack(Visibility.Visible, typeof(int), null!, CultureInfo.InvariantCulture));

    [Fact]
    public void NonZeroToVisible_ConvertBack_Throws() =>
        Assert.Throws<NotSupportedException>(() =>
            _nonZeroConverter.ConvertBack(Visibility.Visible, typeof(int), null!, CultureInfo.InvariantCulture));
}