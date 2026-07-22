using PrMonitor.Models;
using PrMonitor.Services;
using PrMonitor.Settings;
using PrMonitor.ViewModels;
using Xunit;

namespace PrMonitor.Tests.ViewModels;

public class MainViewModelEmptyStateTests
{
    private static readonly string[] KnownHeadlines =
    [
        "All clear. Suspiciously quiet out there.",
        "Zero PRs. Look at you go.",
        "Nothing pending. Feels illegal somehow.",
        "Empty queue. Treat yourself.",
        "No PRs in sight. Weird flex, but okay.",
        "Clean slate. Don't jinx it.",
        "Nada. Zilch. Go outside.",
        "Everything's merged. Suspicious, but I'll allow it.",
        "No reviews needed. The bots are proud of you.",
        "Inbox zero. Achievement unlocked.",
    ];

    [Fact]
    public void InitialState_BeforeAnySnapshot_IsInitialLoadingAndNotEmptyState()
    {
        var vm = CreateViewModel(out _);

        Assert.True(vm.IsInitialLoading);
        Assert.False(vm.IsEmptyState);
    }

    [Fact]
    public void RefreshFromSnapshot_EmptySnapshot_EntersEmptyStateWithKnownHeadline()
    {
        var vm = CreateViewModel(out _);

        vm.RefreshFromSnapshot(new PollSnapshot());

        Assert.False(vm.IsInitialLoading);
        Assert.True(vm.IsEmptyState);
        Assert.Contains(vm.EmptyStateHeadline, KnownHeadlines);
    }

    [Fact]
    public void RefreshFromSnapshot_WithAnyPr_IsNotEmptyState()
    {
        var vm = CreateViewModel(out _);

        vm.RefreshFromSnapshot(new PollSnapshot { AutoMergePrs = [MakePr()] });

        Assert.False(vm.IsInitialLoading);
        Assert.False(vm.IsEmptyState);
    }

    [Fact]
    public void RefreshFromSnapshot_OnlyLaterSnoozedPr_IsNotEmptyState()
    {
        var pr = MakePr();
        var vm = CreateViewModel(out var settings);
        settings.HiddenPrKeys.Add(pr.Key);
        settings.SnoozedPrs[pr.Key] = DateTimeOffset.MaxValue;

        vm.RefreshFromSnapshot(new PollSnapshot { AutoMergePrs = [pr] });

        Assert.Equal(0, vm.AutoMergeCount);
        Assert.Equal(1, vm.HiddenCount);
        Assert.False(vm.IsEmptyState);
    }

    [Fact]
    public void RefreshFromSnapshot_HeadlineStaysStableWhileEmptyAcrossPolls()
    {
        var vm = CreateViewModel(out _);
        vm.RefreshFromSnapshot(new PollSnapshot());
        var headline = vm.EmptyStateHeadline;

        vm.RefreshFromSnapshot(new PollSnapshot());

        Assert.Equal(headline, vm.EmptyStateHeadline);
    }

    private static MainViewModel CreateViewModel(out AppSettings settings)
    {
        settings = new AppSettings();
        return new MainViewModel(settings, new NotificationService(settings), new UpdateService(DiagnosticsLogger.Null));
    }

    private static PullRequestInfo MakePr() =>
        new()
        {
            Number = 1,
            Title = "PR",
            Url = "https://github.com/org/repo/pull/1",
            Repository = "org/repo",
            Author = "alice",
            CIState = CIState.Success,
        };
}
