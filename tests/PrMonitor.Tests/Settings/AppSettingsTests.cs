using System.IO;
using PrMonitor.Models;
using PrMonitor.Settings;
using Xunit;

namespace PrMonitor.Tests.Settings;

public class AppSettingsTests
{
    [Fact]
    public void LoadFrom_MissingFile_ReturnsDefaultSettings()
    {
        var path = TempPath();
        var result = AppSettings.LoadFrom(path);

        Assert.Empty(result.Organizations);
        Assert.Equal(120, result.PollingIntervalSeconds);
        Assert.True(result.AutoMergeExpanded);
        Assert.True(result.ReviewExpanded);
    }

    [Fact]
    public void SaveTo_LoadFrom_RoundTrip_PreservesAllSettings()
    {
        var path = TempPath();
        try
        {
            var settings = new AppSettings
            {
                Organizations = ["org-a", "org-b"],
                PollingIntervalSeconds = 60,
                GitHubUsername = "alice",
                AutoMergeExpanded = false,
                ReviewExpanded = true,
                NotifyCiFailed = false,
                FlakinessAnalysisEnabled = true,
                FlakinessMaxReruns = 5,
            };

            settings.SaveTo(path);
            var loaded = AppSettings.LoadFrom(path);

            Assert.Equal(new[] { "org-a", "org-b" }, loaded.Organizations);
            Assert.Equal(60, loaded.PollingIntervalSeconds);
            Assert.Equal("alice", loaded.GitHubUsername);
            Assert.False(loaded.AutoMergeExpanded);
            Assert.True(loaded.ReviewExpanded);
            Assert.False(loaded.NotifyCiFailed);
            Assert.True(loaded.FlakinessAnalysisEnabled);
            Assert.Equal(5, loaded.FlakinessMaxReruns);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void LoadFrom_PrunesRerunRecordsOlderThan30Days()
    {
        var path = TempPath();
        try
        {
            var settings = new AppSettings
            {
                FlakinessRerunCounts = new Dictionary<string, RerunRecord>
                {
                    ["org/repo#1"] = new RerunRecord { Count = 2, LastAttempt = DateTimeOffset.UtcNow.AddDays(-31) },
                    ["org/repo#2"] = new RerunRecord { Count = 1, LastAttempt = DateTimeOffset.UtcNow.AddDays(-1) },
                },
            };
            settings.SaveTo(path);

            var loaded = AppSettings.LoadFrom(path);

            Assert.False(loaded.FlakinessRerunCounts.ContainsKey("org/repo#1"));
            Assert.True(loaded.FlakinessRerunCounts.ContainsKey("org/repo#2"));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void LoadFrom_KeepsRecentRerunRecords()
    {
        var path = TempPath();
        try
        {
            var settings = new AppSettings
            {
                FlakinessRerunCounts = new Dictionary<string, RerunRecord>
                {
                    ["org/repo#3"] = new RerunRecord { Count = 3, LastAttempt = DateTimeOffset.UtcNow.AddHours(-1) },
                },
            };
            settings.SaveTo(path);

            var loaded = AppSettings.LoadFrom(path);

            Assert.Equal(3, loaded.FlakinessRerunCounts["org/repo#3"].Count);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void SaveTo_UsesCamelCasePropertyNames()
    {
        var path = TempPath();
        try
        {
            new AppSettings { GitHubUsername = "alice" }.SaveTo(path);
            var json = File.ReadAllText(path);

            Assert.Contains("\"gitHubUsername\"", json);
            Assert.Contains("\"alice\"", json);
            Assert.DoesNotContain("\"GitHubUsername\"", json);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void LoadFrom_InvalidJson_ReturnsDefaultSettings()
    {
        var path = TempPath();
        try
        {
            File.WriteAllText(path, "not valid json");
            var result = AppSettings.LoadFrom(path);

            Assert.Equal(120, result.PollingIntervalSeconds);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void LoadFrom_EmptyFile_ReturnsDefaultSettings()
    {
        var path = TempPath();
        try
        {
            File.WriteAllText(path, "");
            var result = AppSettings.LoadFrom(path);

            Assert.Equal(120, result.PollingIntervalSeconds);
        }
        finally { File.Delete(path); }
    }

    private static string TempPath() =>
        Path.Combine(Path.GetTempPath(), $"prtests_{Guid.NewGuid()}.json");
}