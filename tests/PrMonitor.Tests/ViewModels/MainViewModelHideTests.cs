using System.IO;
using PrMonitor.Models;
using PrMonitor.Services;
using PrMonitor.Settings;
using PrMonitor.ViewModels;
using Xunit;

namespace PrMonitor.Tests.ViewModels;

/// <summary>
/// Hiding and restoring a row takes effect immediately, in every section, without waiting for
/// the next poll.
/// </summary>
public class MainViewModelHideTests : IDisposable
{
    private readonly string _settingsPath = Path.Combine(Path.GetTempPath(), $"prtests_{Guid.NewGuid()}.json");

    public void Dispose()
    {
        File.Delete(_settingsPath);
        File.Delete(_settingsPath + ".bak");
    }

    public static TheoryData<Action<MainViewModel, string>> HideMethods => new()
    {
        (vm, key) => vm.HideItem(key),       // Move to later
        (vm, key) => vm.HideCompletely(key), // Hide
    };

    [Theory]
    [MemberData(nameof(HideMethods))]
    public void Hide_RowInStacksSection_LeavesStacksAtOnce(Action<MainViewModel, string> hide)
    {
        using var _ = AppSettings.UseSettingsPathOverride(_settingsPath);
        var vm = CreateViewModel();
        vm.UpdateFromSnapshot(new PollSnapshot { MyPrs = [Stacked(1, depth: 0), Stacked(2, depth: 1)] });

        hide(vm, "org/repo#2");

        Assert.Equal([1], vm.StackedPrs.Select(p => p.Number));
        Assert.Equal(1, vm.StackedCount);
    }

    [Theory]
    [MemberData(nameof(HideMethods))]
    public void Hide_OnlyDependabotPr_ZeroesItsCount(Action<MainViewModel, string> hide)
    {
        using var _ = AppSettings.UseSettingsPathOverride(_settingsPath);
        var vm = CreateViewModel();
        vm.UpdateFromSnapshot(new PollSnapshot { DependabotPrs = [Plain(1)] });

        hide(vm, "org/repo#1");

        Assert.Empty(vm.DependabotPrs);
        Assert.Equal(0, vm.DependabotCount);
    }

    [Fact]
    public void HideCompletely_LastPr_ShowsEmptyStateAtOnce()
    {
        using var _ = AppSettings.UseSettingsPathOverride(_settingsPath);
        var vm = CreateViewModel();
        vm.UpdateFromSnapshot(new PollSnapshot { MyPrs = [Plain(1)] });
        var raised = new List<string?>();
        vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        vm.HideCompletely("org/repo#1");

        Assert.True(vm.IsEmptyState);
        Assert.Contains(nameof(MainViewModel.IsEmptyState), raised);
    }

    [Fact]
    public void RestoreItem_ThenSameSnapshot_RebuildsInSnapshotOrder()
    {
        using var _ = AppSettings.UseSettingsPathOverride(_settingsPath);
        var vm = CreateViewModel();
        var snapshot = new PollSnapshot { MyPrs = [Plain(1), Plain(2), Plain(3)] };
        vm.UpdateFromSnapshot(snapshot);
        vm.HideItem("org/repo#1");
        vm.RestoreItem("org/repo#1");
        Assert.Equal([2, 3, 1], vm.MyPrs.Select(p => p.Number)); // restored rows are appended

        vm.UpdateFromSnapshot(snapshot);

        Assert.Equal([1, 2, 3], vm.MyPrs.Select(p => p.Number));
    }

    private static MainViewModel CreateViewModel()
    {
        var settings = new AppSettings();
        return new MainViewModel(settings, new NotificationService(settings), new UpdateService(DiagnosticsLogger.Null));
    }

    private static PullRequestInfo Plain(int number) => new()
    {
        Number = number,
        Title = $"PR {number}",
        Url = $"https://github.com/org/repo/pull/{number}",
        Repository = "org/repo",
        Author = "alice",
        CreatedAt = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
    };

    private static PullRequestInfo Stacked(int number, int depth)
    {
        var pr = Plain(number);
        pr.StackRootKey = "org/repo#1";
        pr.StackDepth = depth;
        pr.StackOrder = depth;
        pr.StackSize = 2;
        pr.StackChainLength = 2;
        if (depth > 0)
        {
            pr.StackParentKey = "org/repo#1";
            pr.StackParentNumber = 1;
            pr.StackParentUrl = "https://github.com/org/repo/pull/1";
        }
        return pr;
    }
}
