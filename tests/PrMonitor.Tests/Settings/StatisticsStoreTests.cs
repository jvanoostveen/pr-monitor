using System.IO;
using PrMonitor.Settings;
using Xunit;

namespace PrMonitor.Tests.Settings;

public class StatisticsStoreTests
{
    [Fact]
    public void Increment_ForDay_ReturnsCount()
    {
        var store = new StatisticsStore();
        var day = new DateOnly(2026, 6, 23);

        store.Increment(day, StatMetric.OwnPrsOpened);
        store.Increment(day, StatMetric.OwnPrsOpened);
        store.Increment(day, StatMetric.CiFailures, 3);

        Assert.Equal(2, store.ForDay(day).OwnPrsOpened);
        Assert.Equal(3, store.ForDay(day).CiFailures);
        Assert.Equal(0, store.ForDay(day).ReviewsCompleted);
    }

    [Fact]
    public void ForWeekOf_SumsMondayThroughSunday()
    {
        var store = new StatisticsStore();
        // Monday 2026-06-22 .. Sunday 2026-06-28
        store.Increment(new DateOnly(2026, 6, 22), StatMetric.ReviewsCompleted); // Monday
        store.Increment(new DateOnly(2026, 6, 28), StatMetric.ReviewsCompleted); // Sunday
        store.Increment(new DateOnly(2026, 6, 29), StatMetric.ReviewsCompleted); // next Monday (excluded)

        var week = store.ForWeekOf(new DateOnly(2026, 6, 24)); // a Wednesday in that week
        Assert.Equal(2, week.ReviewsCompleted);
    }

    [Fact]
    public void ForMonthOf_SumsCalendarMonth()
    {
        var store = new StatisticsStore();
        store.Increment(new DateOnly(2026, 6, 1), StatMetric.OwnPrsMerged);
        store.Increment(new DateOnly(2026, 6, 30), StatMetric.OwnPrsMerged);
        store.Increment(new DateOnly(2026, 7, 1), StatMetric.OwnPrsMerged); // excluded

        var month = store.ForMonthOf(new DateOnly(2026, 6, 15));
        Assert.Equal(2, month.OwnPrsMerged);
    }

    [Fact]
    public void Total_SumsAllDays()
    {
        var store = new StatisticsStore();
        store.Increment(new DateOnly(2026, 1, 1), StatMetric.FlakyReruns);
        store.Increment(new DateOnly(2026, 6, 1), StatMetric.FlakyReruns);
        store.Increment(new DateOnly(2026, 6, 1), StatMetric.RealFailures);

        Assert.Equal(2, store.Total().FlakyReruns);
        Assert.Equal(1, store.Total().RealFailures);
    }

    [Fact]
    public void SaveTo_LoadFrom_RoundTrip_PreservesCounts()
    {
        var path = TempPath();
        try
        {
            var store = StatisticsStore.LoadFrom(path);
            store.Increment(new DateOnly(2026, 6, 23), StatMetric.OwnPrsOpened, 5);
            store.SaveTo(path);

            var loaded = StatisticsStore.LoadFrom(path);
            Assert.Equal(5, loaded.ForDay(new DateOnly(2026, 6, 23)).OwnPrsOpened);
        }
        finally { Cleanup(path); }
    }

    [Fact]
    public void Save_WritesBackToLoadedPath_NotDefault()
    {
        // Guards the test-isolation requirement: a store loaded from a temp path must
        // persist back to that same path, never the real %APPDATA% file.
        var path = TempPath();
        try
        {
            var store = StatisticsStore.LoadFrom(path);
            store.Increment(new DateOnly(2026, 6, 23), StatMetric.CiFailures);
            store.Save(); // parameterless — must use the remembered source path

            Assert.True(File.Exists(path));
            var loaded = StatisticsStore.LoadFrom(path);
            Assert.Equal(1, loaded.ForDay(new DateOnly(2026, 6, 23)).CiFailures);
        }
        finally { Cleanup(path); }
    }

    [Fact]
    public void LoadFrom_PrunesDaysOlderThanRetentionWindow()
    {
        var path = TempPath();
        try
        {
            var store = StatisticsStore.LoadFrom(path);
            var old = DateOnly.FromDateTime(DateTime.Today).AddDays(-600);
            var recent = DateOnly.FromDateTime(DateTime.Today).AddDays(-10);
            store.Increment(old, StatMetric.OwnPrsOpened);
            store.Increment(recent, StatMetric.OwnPrsOpened);
            store.SaveTo(path);

            var loaded = StatisticsStore.LoadFrom(path);
            Assert.Equal(0, loaded.ForDay(old).OwnPrsOpened);
            Assert.Equal(1, loaded.ForDay(recent).OwnPrsOpened);
        }
        finally { Cleanup(path); }
    }

    [Fact]
    public void LoadFrom_MissingFile_ReturnsEmptyStore()
    {
        var store = StatisticsStore.LoadFrom(TempPath());
        Assert.Empty(store.Days);
        Assert.Equal(0, store.Total().OwnPrsOpened);
    }

    private static string TempPath() =>
        Path.Combine(Path.GetTempPath(), $"prstats_{Guid.NewGuid()}.json");

    private static void Cleanup(string path)
    {
        foreach (var p in new[] { path, path + ".bak", path + ".tmp" })
            if (File.Exists(p)) File.Delete(p);
    }
}
