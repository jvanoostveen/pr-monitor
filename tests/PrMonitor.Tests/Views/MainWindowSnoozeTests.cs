using PrMonitor;
using Xunit;

namespace PrMonitor.Tests.Views;

public class MainWindowSnoozeTests
{
    [Fact]
    public void GetNextWeekMondayMorning_WhenNowIsMonday_UsesFollowingWeekMondayAtNine()
    {
        var nowLocal = new DateTime(2026, 4, 27, 8, 15, 0, DateTimeKind.Unspecified);
        var now = new DateTimeOffset(nowLocal, TimeZoneInfo.Local.GetUtcOffset(nowLocal));

        var result = MainWindow.GetNextWeekMondayMorning(now);
        var localResult = result.ToLocalTime();

        Assert.Equal(new DateTime(2026, 5, 4), localResult.Date);
        Assert.Equal(DayOfWeek.Monday, localResult.DayOfWeek);
        Assert.Equal(9, localResult.Hour);
        Assert.Equal(0, localResult.Minute);
    }

    [Fact]
    public void GetNextWeekMondayMorning_WhenNowIsSunday_UsesNextDayMondayAtNine()
    {
        var nowLocal = new DateTime(2026, 5, 3, 22, 0, 0, DateTimeKind.Unspecified);
        var now = new DateTimeOffset(nowLocal, TimeZoneInfo.Local.GetUtcOffset(nowLocal));

        var result = MainWindow.GetNextWeekMondayMorning(now);
        var localResult = result.ToLocalTime();

        Assert.Equal(new DateTime(2026, 5, 4), localResult.Date);
        Assert.Equal(DayOfWeek.Monday, localResult.DayOfWeek);
        Assert.Equal(9, localResult.Hour);
        Assert.Equal(0, localResult.Minute);
    }
}