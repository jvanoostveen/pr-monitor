using PrMonitor.Models;
using PrMonitor.Services;
using PrMonitor.Settings;
using PrMonitor.ViewModels;
using Xunit;

namespace PrMonitor.Tests.ViewModels;

/// <summary>
/// Rebuilding the bound collections regenerates every row's visual tree, which is the app's biggest
/// recurring allocation. These tests pin the "skip when nothing visibly changed" behaviour.
/// </summary>
public class MainViewModelRebuildTests
{
    [Fact]
    public void UpdateFromSnapshot_IdenticalSnapshot_KeepsSameRowInstances()
    {
        var vm = CreateViewModel();
        vm.UpdateFromSnapshot(new PollSnapshot { AutoMergePrs = [MakePr()] });
        var first = vm.AutoMergePrs[0];

        vm.UpdateFromSnapshot(new PollSnapshot { AutoMergePrs = [MakePr()] });

        Assert.Same(first, vm.AutoMergePrs[0]);
    }

    [Fact]
    public void UpdateFromSnapshot_ChangedCiState_RebuildsRows()
    {
        var vm = CreateViewModel();
        vm.UpdateFromSnapshot(new PollSnapshot { AutoMergePrs = [MakePr()] });
        var first = vm.AutoMergePrs[0];

        vm.UpdateFromSnapshot(new PollSnapshot { AutoMergePrs = [MakePr(ci: CIState.Failure)] });

        Assert.NotSame(first, vm.AutoMergePrs[0]);
        Assert.Equal(CIState.Failure, vm.AutoMergePrs[0].CIState);
    }

    [Fact]
    public void UpdateFromSnapshot_ChangedTitle_RebuildsRows()
    {
        var vm = CreateViewModel();
        vm.UpdateFromSnapshot(new PollSnapshot { AutoMergePrs = [MakePr()] });

        vm.UpdateFromSnapshot(new PollSnapshot { AutoMergePrs = [MakePr(title: "Renamed")] });

        Assert.Equal("Renamed", vm.AutoMergePrs[0].Title);
    }

    [Fact]
    public void UpdateFromSnapshot_AddedPr_RebuildsRows()
    {
        var vm = CreateViewModel();
        vm.UpdateFromSnapshot(new PollSnapshot { AutoMergePrs = [MakePr()] });

        vm.UpdateFromSnapshot(new PollSnapshot { AutoMergePrs = [MakePr(), MakePr(number: 2)] });

        Assert.Equal(2, vm.AutoMergeCount);
        Assert.Equal(2, vm.AutoMergePrs.Count);
    }

    [Fact]
    public void UpdateFromSnapshot_RemovedPr_RebuildsRows()
    {
        var vm = CreateViewModel();
        vm.UpdateFromSnapshot(new PollSnapshot { AutoMergePrs = [MakePr(), MakePr(number: 2)] });

        vm.UpdateFromSnapshot(new PollSnapshot { AutoMergePrs = [MakePr()] });

        Assert.Equal(1, vm.AutoMergeCount);
        Assert.Single(vm.AutoMergePrs);
    }

    [Fact]
    public void UpdateFromSnapshot_PrMovedBetweenSections_RebuildsRows()
    {
        var vm = CreateViewModel();
        vm.UpdateFromSnapshot(new PollSnapshot { AutoMergePrs = [MakePr()] });

        vm.UpdateFromSnapshot(new PollSnapshot { MyPrs = [MakePr()] });

        Assert.Empty(vm.AutoMergePrs);
        Assert.Single(vm.MyPrs);
    }

    [Fact]
    public void RefreshFromSnapshot_AlwaysRebuilds_BecauseSettingsMayHaveChanged()
    {
        var vm = CreateViewModel();
        vm.RefreshFromSnapshot(new PollSnapshot { AutoMergePrs = [MakePr()] });
        var first = vm.AutoMergePrs[0];

        vm.RefreshFromSnapshot(new PollSnapshot { AutoMergePrs = [MakePr()] });

        Assert.NotSame(first, vm.AutoMergePrs[0]);
    }

    [Fact]
    public void UpdateFromSnapshot_ChangedLabels_RebuildsRows()
    {
        var vm = CreateViewModel();
        vm.UpdateFromSnapshot(new PollSnapshot { AutoMergePrs = [MakePr()] });
        var first = vm.AutoMergePrs[0];

        vm.UpdateFromSnapshot(new PollSnapshot { AutoMergePrs = [MakePr(labels: ["Prioriteit/High"])] });

        Assert.NotSame(first, vm.AutoMergePrs[0]);
        Assert.True(vm.AutoMergePrs[0].IsPriority);
    }

    [Fact]
    public void UpdateFromSnapshot_PriorityPrs_SortedToTopOfSection_OthersKeepOrder()
    {
        var vm = CreateViewModel();

        vm.UpdateFromSnapshot(new PollSnapshot
        {
            MyPrs =
            [
                MakePr(number: 1),
                MakePr(number: 2, labels: ["Prioriteit/High"]),
                MakePr(number: 3),
                MakePr(number: 4, labels: ["Prioriteit/High"]),
            ],
        });

        Assert.Equal([2, 4, 1, 3], vm.MyPrs.Select(p => p.Number));
    }

    private static MainViewModel CreateViewModel()
    {
        var settings = new AppSettings();
        return new MainViewModel(settings, new NotificationService(settings), new UpdateService(DiagnosticsLogger.Null));
    }

    private static PullRequestInfo MakePr(int number = 1, string title = "PR", CIState ci = CIState.Success,
        IReadOnlyList<string>? labels = null) =>
        new()
        {
            Labels = labels ?? [],
            Number = number,
            Title = title,
            Url = $"https://github.com/org/repo/pull/{number}",
            Repository = "org/repo",
            Author = "alice",
            CIState = ci,
            CreatedAt = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
        };
}
