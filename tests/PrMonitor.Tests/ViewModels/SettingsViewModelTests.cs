using System.IO;
using PrMonitor.Models;
using PrMonitor.Settings;
using PrMonitor.ViewModels;
using Xunit;

namespace PrMonitor.Tests.ViewModels;

public class SettingsViewModelTests
{
    // ── PollingIntervalSeconds minimum clamp ──────────────────────────

    [Fact]
    public void PollingIntervalSeconds_BelowMinimum_ClampedTo30()
    {
        var vm = new SettingsViewModel(MakeSettings());
        vm.PollingIntervalSeconds = 5;

        Assert.Equal(30, vm.PollingIntervalSeconds);
    }

    [Fact]
    public void PollingIntervalSeconds_ExactMinimum_AllowedThrough()
    {
        var vm = new SettingsViewModel(MakeSettings());
        vm.PollingIntervalSeconds = 30;

        Assert.Equal(30, vm.PollingIntervalSeconds);
    }

    [Fact]
    public void PollingIntervalSeconds_AboveMinimum_Accepted()
    {
        var vm = new SettingsViewModel(MakeSettings());
        vm.PollingIntervalSeconds = 300;

        Assert.Equal(300, vm.PollingIntervalSeconds);
    }

    // ── FlakinessMaxReruns clamp ──────────────────────────────────────

    [Fact]
    public void FlakinessMaxReruns_Zero_ClampedToOne()
    {
        var vm = new SettingsViewModel(MakeSettings());
        vm.FlakinessMaxReruns = 0;

        Assert.Equal(1, vm.FlakinessMaxReruns);
    }

    [Fact]
    public void FlakinessMaxReruns_ElevenClampsToTen()
    {
        var vm = new SettingsViewModel(MakeSettings());
        vm.FlakinessMaxReruns = 11;

        Assert.Equal(10, vm.FlakinessMaxReruns);
    }

    [Fact]
    public void FlakinessMaxReruns_ValidValue_AcceptedAsIs()
    {
        var vm = new SettingsViewModel(MakeSettings());
        vm.FlakinessMaxReruns = 5;

        Assert.Equal(5, vm.FlakinessMaxReruns);
    }

    // ── FlakinessCustomHints 500-char truncation ──────────────────────

    [Fact]
    public void FlakinessCustomHints_Under500Chars_AcceptedAsIs()
    {
        var vm = new SettingsViewModel(MakeSettings());
        var value = new string('a', 300);
        vm.FlakinessCustomHints = value;

        Assert.Equal(value, vm.FlakinessCustomHints);
    }

    [Fact]
    public void FlakinessCustomHints_Over500Chars_TruncatedTo500()
    {
        var vm = new SettingsViewModel(MakeSettings());
        vm.FlakinessCustomHints = new string('x', 600);

        Assert.Equal(500, vm.FlakinessCustomHints.Length);
    }

    [Fact]
    public void FlakinessCustomHints_Null_BecomesEmptyString()
    {
        var vm = new SettingsViewModel(MakeSettings());
        vm.FlakinessCustomHints = null!;

        Assert.Equal("", vm.FlakinessCustomHints);
    }

    // ── Notification mode radio logic ─────────────────────────────────

    [Fact]
    public void NotificationModeAlways_WhenSet_OtherModesAreFalse()
    {
        var settings = MakeSettings(mode: NotificationMode.Never);
        var vm = new SettingsViewModel(settings);

        vm.NotificationModeAlways = true;

        Assert.True(vm.NotificationModeAlways);
        Assert.False(vm.NotificationModeWhenWindowClosed);
        Assert.False(vm.NotificationModeNever);
    }

    [Fact]
    public void NotificationModeWhenWindowClosed_WhenSet_OtherModesAreFalse()
    {
        var vm = new SettingsViewModel(MakeSettings(mode: NotificationMode.Always));

        vm.NotificationModeWhenWindowClosed = true;

        Assert.False(vm.NotificationModeAlways);
        Assert.True(vm.NotificationModeWhenWindowClosed);
        Assert.False(vm.NotificationModeNever);
    }

    [Fact]
    public void NotificationModeNever_WhenSet_IndividualTogglesDisabled()
    {
        var vm = new SettingsViewModel(MakeSettings());
        vm.NotificationModeNever = true;

        Assert.False(vm.IndividualTogglesEnabled);
    }

    [Fact]
    public void NotificationModeAlways_IndividualTogglesEnabled()
    {
        var vm = new SettingsViewModel(MakeSettings(mode: NotificationMode.Always));
        Assert.True(vm.IndividualTogglesEnabled);
    }

    [Fact]
    public void NotificationModeWhenWindowClosed_IndividualTogglesEnabled()
    {
        var vm = new SettingsViewModel(MakeSettings(mode: NotificationMode.WhenWindowClosed));
        Assert.True(vm.IndividualTogglesEnabled);
    }

    [Fact]
    public void NotificationModeNever_InitializedFromSettings_ReflectsCorrectly()
    {
        var vm = new SettingsViewModel(MakeSettings(mode: NotificationMode.Never));

        Assert.False(vm.NotificationModeAlways);
        Assert.False(vm.NotificationModeWhenWindowClosed);
        Assert.True(vm.NotificationModeNever);
    }

    // ── DeleteRule ────────────────────────────────────────────────────

    [Fact]
    public void DeleteRule_ExistingId_RemovesRule()
    {
        var settings = MakeSettings();
        settings.FlakinessRules.Add(new FlakinessRule { Id = "rule-1", Pattern = "timeout", Description = "Timeout" });
        var vm = new SettingsViewModel(settings);

        vm.DeleteRule("rule-1");

        Assert.Empty(vm.FlakinessRules);
    }

    [Fact]
    public void DeleteRule_UnknownId_IsNoOp()
    {
        var settings = MakeSettings();
        settings.FlakinessRules.Add(new FlakinessRule { Id = "rule-1", Pattern = "timeout", Description = "Timeout" });
        var vm = new SettingsViewModel(settings);

        vm.DeleteRule("nonexistent");

        Assert.Single(vm.FlakinessRules);
    }

    [Fact]
    public void DeleteRule_OnlyRemovesMatchingRule()
    {
        var settings = MakeSettings();
        settings.FlakinessRules.Add(new FlakinessRule { Id = "rule-1", Pattern = "timeout", Description = "Timeout" });
        settings.FlakinessRules.Add(new FlakinessRule { Id = "rule-2", Pattern = "flaky",   Description = "Flaky"   });
        var vm = new SettingsViewModel(settings);

        vm.DeleteRule("rule-1");

        Assert.Single(vm.FlakinessRules);
        Assert.Equal("rule-2", vm.FlakinessRules[0].Id);
    }

    // ── Initialization from settings ─────────────────────────────────

    [Fact]
    public void Constructor_MultipleOrgs_JoinsWithNewlines()
    {
        var settings = MakeSettings();
        settings.Organizations = ["org1", "org2", "org3"];
        var vm = new SettingsViewModel(settings);

        var lines = vm.OrganizationsText.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(["org1", "org2", "org3"], lines);
    }

    [Fact]
    public void Constructor_FlakinessRules_LoadedIntoObservableCollection()
    {
        var settings = MakeSettings();
        settings.FlakinessRules.Add(new FlakinessRule { Id = "r1", Pattern = "timeout", Description = "Timeout" });
        settings.FlakinessRules.Add(new FlakinessRule { Id = "r2", Pattern = "flaky",   Description = "Flaky"   });
        var vm = new SettingsViewModel(settings);

        Assert.Equal(2, vm.FlakinessRules.Count);
        Assert.Equal("r1", vm.FlakinessRules[0].Id);
        Assert.Equal("r2", vm.FlakinessRules[1].Id);
    }

    [Fact]
    public void Constructor_HiddenPrs_UsesExplicitManualHiddenList()
    {
        var settings = MakeSettings();
        settings.HiddenPrKeys = ["org/repo#1", "org/repo#2"];
        settings.ManuallyHiddenPrKeys = ["org/repo#1"];
        settings.SnoozedPrs["org/repo#2"] = DateTimeOffset.MaxValue;

        var vm = new SettingsViewModel(settings);

        Assert.Single(vm.HiddenPrs);
        Assert.Equal("org/repo#1", vm.HiddenPrs[0].Key);
    }

    [Fact]
    public void RemoveHiddenPr_ExistingKey_RemovesFromCollection()
    {
        var settings = MakeSettings();
        settings.HiddenPrKeys = ["org/repo#1"];
        settings.ManuallyHiddenPrKeys = ["org/repo#1"];
        var vm = new SettingsViewModel(settings);

        vm.RemoveHiddenPr("org/repo#1");

        Assert.Empty(vm.HiddenPrs);
    }

    [Fact]
    public void Save_RemovedManualHiddenPr_IsRemovedButSnoozedStaysHidden()
    {
        var path = TempPath();
        using var _ = AppSettings.UseSettingsPathOverride(path);

        var settings = MakeSettings();
        settings.HiddenPrKeys = ["org/repo#1", "org/repo#2"];
        settings.ManuallyHiddenPrKeys = ["org/repo#1"];
        settings.SnoozedPrs["org/repo#2"] = DateTimeOffset.MaxValue;

        var vm = new SettingsViewModel(settings);
        vm.RemoveHiddenPr("org/repo#1");

        vm.Save();

        Assert.DoesNotContain("org/repo#1", settings.HiddenPrKeys);
        Assert.Contains("org/repo#2", settings.HiddenPrKeys);
        Assert.DoesNotContain("org/repo#1", settings.ManuallyHiddenPrKeys);

        File.Delete(path);
        File.Delete(path + ".bak");
    }

    [Fact]
    public void Save_PreservesFlakinessRuleMetadata()
    {
        var path = TempPath();
        using var _ = AppSettings.UseSettingsPathOverride(path);

        var settings = MakeSettings();
        settings.FlakinessRules.Add(new FlakinessRule
        {
            Id = "rule-1",
            Pattern = "timeout",
            Description = "Timeout rule",
            IsEnabled = true,
            CreatedAt = new DateTimeOffset(2026, 4, 1, 12, 0, 0, TimeSpan.Zero),
            MatchCount = 5,
        });

        var vm = new SettingsViewModel(settings);
        vm.Save();

        Assert.Single(settings.FlakinessRules);
        Assert.Equal(new DateTimeOffset(2026, 4, 1, 12, 0, 0, TimeSpan.Zero), settings.FlakinessRules[0].CreatedAt);
        Assert.Equal(5, settings.FlakinessRules[0].MatchCount);

        File.Delete(path);
        File.Delete(path + ".bak");
    }

    // ── Helpers ──────────────────────────────────────────────────────

    private static AppSettings MakeSettings(NotificationMode mode = NotificationMode.Always) =>
        new() { NotificationMode = mode };

    private static string TempPath() =>
        Path.Combine(Path.GetTempPath(), $"prtests_settingsvm_{Guid.NewGuid()}.json");
}

