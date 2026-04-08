using System.Globalization;
using PrMonitor.Converters;
using Xunit;

namespace PrMonitor.Tests.Converters;

public class BoolToAngleConverterTests
{
    private readonly BoolToAngleConverter _converter = new();

    [Theory]
    [InlineData(true, 90.0)]
    [InlineData(false, 0.0)]
    public void Convert_BoolInput_ReturnsExpectedAngle(bool input, double expectedAngle)
    {
        var result = _converter.Convert(input, typeof(double), null!, CultureInfo.InvariantCulture);
        Assert.Equal(expectedAngle, result);
    }

    [Theory]
    [InlineData("not a bool")]
    [InlineData(42)]
    public void Convert_NonBoolInput_ReturnsZero(object input)
    {
        var result = _converter.Convert(input, typeof(double), null!, CultureInfo.InvariantCulture);
        Assert.Equal(0.0, result);
    }

    [Fact]
    public void ConvertBack_ThrowsNotSupportedException()
    {
        Assert.Throws<NotSupportedException>(() =>
            _converter.ConvertBack(0.0, typeof(bool), null!, CultureInfo.InvariantCulture));
    }
}
