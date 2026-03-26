using System.Globalization;
using System.Windows.Media;
using PrMonitor.Converters;
using PrMonitor.Models;
using Xunit;

namespace PrMonitor.Tests.Converters;

public class CIStateToBrushConverterTests
{
    private readonly CIStateToBrushConverter _converter = new();

    [Theory]
    [InlineData(CIState.Success, "#3FB950")]
    [InlineData(CIState.Failure, "#F85149")]
    [InlineData(CIState.Pending, "#D29922")]
    [InlineData(CIState.Error,   "#F0883E")]
    [InlineData(CIState.Unknown, "#484F58")]
    public void Convert_CIState_ReturnsCorrectColor(CIState state, string expectedHex)
    {
        var result = _converter.Convert(state, typeof(SolidColorBrush), null!, CultureInfo.InvariantCulture);

        var brush = Assert.IsType<SolidColorBrush>(result);
        var expected = (Color)ColorConverter.ConvertFromString(expectedHex);
        Assert.Equal(expected.R, brush.Color.R);
        Assert.Equal(expected.G, brush.Color.G);
        Assert.Equal(expected.B, brush.Color.B);
    }

    [Fact]
    public void Convert_NonCIStateInput_ReturnsDimGrayBrush()
    {
        var result = _converter.Convert("not a state", typeof(SolidColorBrush), null!, CultureInfo.InvariantCulture);

        var brush = Assert.IsType<SolidColorBrush>(result);
        var dimGray = (Color)ColorConverter.ConvertFromString("#484F58");
        Assert.Equal(dimGray.R, brush.Color.R);
        Assert.Equal(dimGray.G, brush.Color.G);
        Assert.Equal(dimGray.B, brush.Color.B);
    }

    [Fact]
    public void ConvertBack_ThrowsNotSupportedException()
    {
        Assert.Throws<NotSupportedException>(() =>
            _converter.ConvertBack(null!, typeof(CIState), null!, CultureInfo.InvariantCulture));
    }
}