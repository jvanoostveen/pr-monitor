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
    public void LoadFrom_InvalidJson_UsesBackupWhenAvailable()
    {
        var path = TempPath();
        var backupPath = path + ".bak";
        try
        {
            var backup = new AppSettings
            {
                PollingIntervalSeconds = 45,
                GitHubUsername = "backup-user",
            };
            backup.SaveTo(backupPath);

            File.WriteAllText(path, "{ invalid json }");

            var loaded = AppSettings.LoadFrom(path);

            Assert.Equal(45, loaded.PollingIntervalSeconds);
            Assert.Equal("backup-user", loaded.GitHubUsername);
        }
        finally
        {
            File.Delete(path);
            File.Delete(backupPath);
        }
    }

    [Fact]
    public void SaveTo_WhenFileExists_CreatesBackupFile()
    {
        var path = TempPath();
        var backupPath = path + ".bak";
        try
        {
            new AppSettings { PollingIntervalSeconds = 60 }.SaveTo(path);

            new AppSettings { PollingIntervalSeconds = 90 }.SaveTo(path);

            Assert.True(File.Exists(backupPath));
            var backupLoaded = AppSettings.LoadFrom(backupPath);
            Assert.Equal(60, backupLoaded.PollingIntervalSeconds);
        }
        finally
        {
            File.Delete(path);
            File.Delete(backupPath);
        }
    }

        [Fact]
        public void LoadFrom_UnknownNotificationMode_PreservesFlakinessSettings()
        {
                var path = TempPath();
                try
                {
                        File.WriteAllText(path,
                                """
                                {
                                    "notificationMode": "LegacyUnknownValue",
                                    "flakinessCustomHints": "Please treat browser retries as flaky",
                                    "flakinessRules": [
                                        {
                                            "id": "rule-1",
                                            "pattern": "timeout",
                                            "description": "Timeout rule",
                                            "isEnabled": true,
                                            "createdAt": "2026-04-01T12:00:00Z",
                                            "matchCount": 7
                                        }
                                    ]
                                }
                                """);

                        var loaded = AppSettings.LoadFrom(path);

                        Assert.Equal(NotificationMode.Always, loaded.NotificationMode);
                        Assert.Equal("Please treat browser retries as flaky", loaded.FlakinessCustomHints);
                        Assert.Single(loaded.FlakinessRules);
                        Assert.Equal("rule-1", loaded.FlakinessRules[0].Id);
                        Assert.Equal(7, loaded.FlakinessRules[0].MatchCount);
                }
                finally { File.Delete(path); }
        }

        [Fact]
        public void LoadFrom_LegacyNotificationModeAlias_MapsToWhenWindowClosed()
        {
                var path = TempPath();
                try
                {
                        File.WriteAllText(path,
                                """
                                {
                                    "notificationMode": "OnlyWhenWindowClosed"
                                }
                                """);

                        var loaded = AppSettings.LoadFrom(path);

                        Assert.Equal(NotificationMode.WhenWindowClosed, loaded.NotificationMode);
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

    // ── Snooze pruning ───────────────────────────────────────────────

    [Fact]
    public void LoadFrom_PrunesExpiredSnoozedPrs()
    {
        var path = TempPath();
        try
        {
            var settings = new AppSettings
            {
                SnoozedPrs = new Dictionary<string, DateTimeOffset>
                {
                    ["org/repo#1"] = DateTimeOffset.UtcNow.AddMinutes(-5),   // expired
                    ["org/repo#2"] = DateTimeOffset.UtcNow.AddHours(2),      // still active
                },
            };
            settings.SaveTo(path);

            var loaded = AppSettings.LoadFrom(path);

            Assert.False(loaded.SnoozedPrs.ContainsKey("org/repo#1"));
            Assert.True(loaded.SnoozedPrs.ContainsKey("org/repo#2"));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void LoadFrom_KeepsIndefiniteSnooze()
    {
        var path = TempPath();
        try
        {
            var settings = new AppSettings
            {
                SnoozedPrs = new Dictionary<string, DateTimeOffset>
                {
                    ["org/repo#42"] = DateTimeOffset.MaxValue,
                },
            };
            settings.SaveTo(path);

            var loaded = AppSettings.LoadFrom(path);

            Assert.True(loaded.SnoozedPrs.ContainsKey("org/repo#42"));
            Assert.Equal(DateTimeOffset.MaxValue, loaded.SnoozedPrs["org/repo#42"]);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void LoadFrom_EmptySnoozedPrs_LoadsWithoutError()
    {
        var path = TempPath();
        try
        {
            new AppSettings().SaveTo(path);
            var loaded = AppSettings.LoadFrom(path);

            Assert.Empty(loaded.SnoozedPrs);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void LoadFrom_MigratesLegacyHiddenPrKeysToIndefiniteSnooze()
    {
        var path = TempPath();
        try
        {
            var settings = new AppSettings
            {
                HiddenPrKeys = ["org/repo#1", "org/repo#2"],
            };
            settings.SaveTo(path);

            var loaded = AppSettings.LoadFrom(path);

            Assert.Equal(DateTimeOffset.MaxValue, loaded.SnoozedPrs["org/repo#1"]);
            Assert.Equal(DateTimeOffset.MaxValue, loaded.SnoozedPrs["org/repo#2"]);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void LoadFrom_DoesNotMigrateExplicitlyManualHiddenPrs()
    {
        var path = TempPath();
        try
        {
            var settings = new AppSettings
            {
                HiddenPrKeys = ["org/repo#99"],
                ManuallyHiddenPrKeys = ["org/repo#99"],
            };
            settings.SaveTo(path);

            var loaded = AppSettings.LoadFrom(path);

            Assert.DoesNotContain("org/repo#99", loaded.SnoozedPrs.Keys);
            Assert.Contains("org/repo#99", loaded.ManuallyHiddenPrKeys);
        }
        finally { File.Delete(path); }
    }

    // ── New fields (reviewer assignment) ────────────────────────────────

    [Fact]
    public void SaveTo_LoadFrom_RoundTrip_RecentReviewers()
    {
        var path = TempPath();
        try
        {
            var settings = new AppSettings
            {
                RecentReviewers = ["alice", "bob", "carol"],
            };
            settings.SaveTo(path);
            var loaded = AppSettings.LoadFrom(path);

            Assert.Equal(new[] { "alice", "bob", "carol" }, loaded.RecentReviewers);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void SaveTo_LoadFrom_RoundTrip_OrgMembersCache()
    {
        var path = TempPath();
        try
        {
            var now = new DateTimeOffset(2026, 4, 1, 12, 0, 0, TimeSpan.Zero);
            var settings = new AppSettings
            {
                OrgMembersCache =
                [
                    new OrgMemberEntry { Login = "alice", Name = "Alice Smith" },
                    new OrgMemberEntry { Login = "bob",   Name = null },
                ],
                OrgMembersCachedAt = now,
            };
            settings.SaveTo(path);
            var loaded = AppSettings.LoadFrom(path);

            Assert.Equal(2, loaded.OrgMembersCache.Count);
            Assert.Equal("alice", loaded.OrgMembersCache[0].Login);
            Assert.Equal("Alice Smith", loaded.OrgMembersCache[0].Name);
            Assert.Equal("bob", loaded.OrgMembersCache[1].Login);
            Assert.Null(loaded.OrgMembersCache[1].Name);
            Assert.Equal(now, loaded.OrgMembersCachedAt);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void DefaultSettings_DraftExpandedFalse_DependabotExpandedFalse()
    {
        var settings = new AppSettings();

        Assert.False(settings.DraftExpanded);
        Assert.False(settings.DependabotExpanded);
    }

    private static string TempPath() =>
        Path.Combine(Path.GetTempPath(), $"prtests_{Guid.NewGuid()}.json");
}