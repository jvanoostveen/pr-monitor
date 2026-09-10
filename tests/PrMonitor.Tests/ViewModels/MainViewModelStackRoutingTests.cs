using PrMonitor.Models;
using PrMonitor.Services;
using PrMonitor.Settings;
using PrMonitor.ViewModels;
using Xunit;

namespace PrMonitor.Tests.ViewModels;

/// <summary>
/// The Stacks section must not outrank why a PR is listed: a PR you were asked to review — directly,
/// through a team, or as hotfix assignee — stays in its own section and is only grouped there.
/// </summary>
public class MainViewModelStackRoutingTests
{
    [Fact]
    public void UpdateFromSnapshot_StackedTeamReviewPr_StaysInTeamReviewSection()
    {
        var vm = CreateViewModel();

        vm.UpdateFromSnapshot(new PollSnapshot
        {
            TeamReviewRequestedPrs = [Stacked(1, depth: 0), Stacked(2, depth: 1)],
        });

        Assert.Equal(2, vm.TeamReviewCount);
        Assert.Equal([1, 2], vm.TeamReviewRequestedPrs.Select(p => p.Number));
        Assert.Empty(vm.StackedPrs);
        Assert.Equal(0, vm.StackedCount);
    }

    [Fact]
    public void UpdateFromSnapshot_StackedReviewRequestedPr_StaysInReviewSection()
    {
        var vm = CreateViewModel();

        vm.UpdateFromSnapshot(new PollSnapshot { ReviewRequestedPrs = [Stacked(1, depth: 0), Stacked(2, depth: 1)] });

        Assert.Equal(2, vm.ReviewCount);
        Assert.Empty(vm.StackedPrs);
    }

    [Fact]
    public void UpdateFromSnapshot_StackedHotfixPr_StaysInHotfixSection()
    {
        var vm = CreateViewModel();

        vm.UpdateFromSnapshot(new PollSnapshot { HotfixPrs = [Stacked(1, depth: 0), Stacked(2, depth: 1)] });

        Assert.Equal(2, vm.HotfixCount);
        Assert.Empty(vm.StackedPrs);
    }

    [Theory]
    [InlineData("MyPrs")]
    [InlineData("AutoMergePrs")]
    [InlineData("DraftPrs")]
    [InlineData("DependabotPrs")]
    public void UpdateFromSnapshot_StackedPrWithoutReviewClaim_StillMovesToStacksSection(string section)
    {
        var vm = CreateViewModel();
        List<PullRequestInfo> prs = [Stacked(1, depth: 0), Stacked(2, depth: 1)];
        var snapshot = section switch
        {
            "MyPrs" => new PollSnapshot { MyPrs = prs },
            "AutoMergePrs" => new PollSnapshot { AutoMergePrs = prs },
            "DraftPrs" => new PollSnapshot { DraftPrs = prs },
            _ => new PollSnapshot { DependabotPrs = prs },
        };

        vm.UpdateFromSnapshot(snapshot);

        Assert.Equal(2, vm.StackedCount);
        Assert.Equal([1, 2], vm.StackedPrs.Select(p => p.Number));
    }

    [Fact]
    public void UpdateFromSnapshot_StackedReviewPr_IsIndentedButShowsNoSeparator()
    {
        var vm = CreateViewModel();

        vm.UpdateFromSnapshot(new PollSnapshot
        {
            TeamReviewRequestedPrs = [Stacked(1, depth: 0), Stacked(2, depth: 1), Plain(9)],
        });

        Assert.Equal(0, vm.TeamReviewRequestedPrs[0].StackIndentMargin.Left);
        Assert.Equal(14, vm.TeamReviewRequestedPrs[1].StackIndentMargin.Left);
        Assert.Equal(0, vm.TeamReviewRequestedPrs[2].StackIndentMargin.Left);
        Assert.All(vm.TeamReviewRequestedPrs, p => Assert.False(p.ShowStackGroupSeparator));
    }

    [Fact]
    public void UpdateFromSnapshot_StackRelationsDisabled_LeavesReviewSectionFlat()
    {
        var settings = new AppSettings { ShowStackRelations = false };
        var vm = new MainViewModel(settings, new NotificationService(settings), new UpdateService(DiagnosticsLogger.Null));

        vm.UpdateFromSnapshot(new PollSnapshot { TeamReviewRequestedPrs = [Stacked(1, depth: 0), Stacked(2, depth: 1)] });

        Assert.All(vm.TeamReviewRequestedPrs, p => Assert.Equal(0, p.StackIndentMargin.Left));
    }

    [Fact]
    public void ApplyInlineStackGrouping_PullsStackMembersToTheFirstMembersPosition()
    {
        List<PrItemViewModel> items =
        [
            Item(Plain(5)),
            Item(Stacked(2, depth: 1)),
            Item(Plain(6)),
            Item(Stacked(1, depth: 0)),
        ];

        var ordered = MainViewModel.ApplyInlineStackGrouping(items);

        Assert.Equal([5, 1, 2, 6], ordered.Select(i => i.Number));
    }

    [Fact]
    public void ApplyInlineStackGrouping_UnstackedRowsStayFlush()
    {
        var ordered = MainViewModel.ApplyInlineStackGrouping([Item(Plain(5)), Item(Plain(6))]);

        Assert.All(ordered, i => Assert.True(i.IsStackGroupStart));
        Assert.All(ordered, i => Assert.Equal(0, i.StackIndentMargin.Left));
    }

    private static MainViewModel CreateViewModel()
    {
        var settings = new AppSettings();
        return new MainViewModel(settings, new NotificationService(settings), new UpdateService(DiagnosticsLogger.Null));
    }

    private static PrItemViewModel Item(PullRequestInfo pr) => PrItemViewModel.From(pr);

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
        pr.StackSize = 2;
        if (depth > 0)
        {
            pr.StackParentKey = "org/repo#1";
            pr.StackParentNumber = 1;
            pr.StackParentUrl = "https://github.com/org/repo/pull/1";
        }
        return pr;
    }
}
