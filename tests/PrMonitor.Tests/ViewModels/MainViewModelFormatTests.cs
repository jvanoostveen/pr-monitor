using PrMonitor.ViewModels;
using Xunit;

namespace PrMonitor.Tests.ViewModels;

public class MainViewModelFormatTests
{
    // ── FormatSnoozedUntil ────────────────────────────────────────────

    [Fact]
    public void FormatSnoozedUntil_MaxValue_ReturnsInfinity()
    {
        var result = MainViewModel.FormatSnoozedUntil(DateTimeOffset.MaxValue);
        Assert.Equal("∞", result);
    }

    [Fact]
    public void FormatSnoozedUntil_Within90Minutes_ReturnsPlusMinutes()
    {
        var until = DateTimeOffset.Now.AddMinutes(45);
        var result = MainViewModel.FormatSnoozedUntil(until);

        // diff.TotalMinutes ≈ 45, so result should be "Until 46m"
        Assert.Matches(@"^Until \d+m$", result);
    }

    [Fact]
    public void FormatSnoozedUntil_BetweenOneAndTwentyFourHours_ReturnsHHmm()
    {
        var until = DateTimeOffset.Now.AddHours(5);
        var local = until.ToLocalTime();
        var result = MainViewModel.FormatSnoozedUntil(until);

        Assert.Equal($"Until {local:HH:mm}", result);
    }

    [Fact]
    public void FormatSnoozedUntil_BetweenOneDayAndOneWeek_ReturnsDayOfWeekAndTime()
    {
        var until = DateTimeOffset.Now.AddDays(3);
        var local = until.ToLocalTime();
        var result = MainViewModel.FormatSnoozedUntil(until);

        Assert.Equal($"Until {local:ddd HH:mm}", result);
    }

    [Fact]
    public void FormatSnoozedUntil_MoreThanOneWeek_ReturnsMonthAndDay()
    {
        var until = DateTimeOffset.Now.AddDays(14);
        var local = until.ToLocalTime();
        var result = MainViewModel.FormatSnoozedUntil(until);

        Assert.Equal($"Until {local:MMM d}", result);
    }
}
