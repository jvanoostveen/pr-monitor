using PrMonitor.Models;
using PrMonitor.Services;
using PrMonitor.ViewModels;
using Xunit;

namespace PrMonitor.Tests.ViewModels;

public class MainViewModelStackSectionTests
{
    [Fact]
    public void OrderStackSection_GroupsStacksAndSortsBottomFirst()
    {
        var items = new[]
        {
            Item(3, root: "org/repo#1", depth: 2),
            Item(9, root: "org/repo#8", depth: 1),
            Item(1, root: "org/repo#1", depth: 0),
            Item(2, root: "org/repo#1", depth: 1),
            Item(8, root: "org/repo#8", depth: 0),
        };

        var ordered = MainViewModel.OrderStackSection(items);

        Assert.Equal([1, 2, 3, 8, 9], ordered.Select(i => i.Number));
    }

    [Fact]
    public void OrderStackSection_MarksSeparatorOnEveryStackButTheFirst()
    {
        var items = new[]
        {
            Item(1, root: "org/repo#1", depth: 0),
            Item(2, root: "org/repo#1", depth: 1),
            Item(8, root: "org/repo#8", depth: 0),
        };

        var ordered = MainViewModel.OrderStackSection(items);

        Assert.False(ordered[0].ShowStackGroupSeparator);
        Assert.False(ordered[1].ShowStackGroupSeparator);
        Assert.True(ordered[2].ShowStackGroupSeparator);
    }

    [Fact]
    public void OrderStackSection_IndentsEveryRowExceptEachGroupsFirst()
    {
        var items = new[]
        {
            Item(1, root: "org/repo#1", depth: 0),
            Item(2, root: "org/repo#1", depth: 1),
            Item(8, root: "org/repo#8", depth: 0),
        };

        var ordered = MainViewModel.OrderStackSection(items);

        Assert.Equal(0, ordered[0].StackIndentMargin.Left);
        Assert.Equal(14, ordered[1].StackIndentMargin.Left);
        Assert.Equal(0, ordered[2].StackIndentMargin.Left);
    }

    [Fact]
    public void BuildStackChainTooltip_ListsEveryMemberAndMarksCurrent()
    {
        var bottom = Pr(41, depth: 0, author: "alice", state: CIState.Success);
        var middle = Pr(42, depth: 1, author: "bob", state: CIState.Failure);
        var top = Pr(43, depth: 2, author: "bob", isDraft: true);
        List<PullRequestInfo> members = [bottom, middle, top];

        var tooltip = MainViewModel.BuildStackChainTooltip(middle, members);
        var lines = tooltip.Split(Environment.NewLine);

        Assert.Equal("Stack (3 PRs):", lines[0]);
        Assert.Contains("1/3", lines[1]);
        Assert.Contains("#41 alice — Success", lines[1]);
        Assert.StartsWith(" \u25b8", lines[2]);
        Assert.Contains("#42 bob — Failure", lines[2]);
        Assert.Contains("#43 bob — Draft", lines[3]);
        Assert.DoesNotContain("\u25b8", lines[3]);
    }

    [Fact]
    public void StackLineage_LeavesOutSiblingBranches()
    {
        List<PullRequestInfo> prs =
        [
            Linked(1, "main", "base"),
            Linked(11, "base", "a1"),
            Linked(12, "a1", "a2"),
            Linked(20, "base", "b1"),
        ];
        PollingService.ApplyStackRelations(prs);
        var members = PollingService.OrderByStack(prs);

        Assert.Equal([1, 11, 12], MainViewModel.StackLineage(prs[1], members).Select(p => p.Number));
        Assert.Equal([1, 11, 12, 20], MainViewModel.StackLineage(prs[0], members).Select(p => p.Number));
    }

    [Fact]
    public void BuildStackChainTooltip_Tree_ShowsBranchCountAndIndentsBranches()
    {
        List<PullRequestInfo> prs =
        [
            Linked(1, "main", "base"),
            Linked(11, "base", "a1"),
            Linked(20, "base", "b1"),
        ];
        PollingService.ApplyStackRelations(prs);

        var lines = MainViewModel.BuildStackChainTooltip(prs[0], prs).Split(Environment.NewLine);

        Assert.Equal("Stack (3 PRs, 2 branches):", lines[0]);
        Assert.Contains("1 · 2 branches  #1", lines[1]);
        Assert.StartsWith("      2/2  #11", lines[2]);
    }

    [Fact]
    public void OrderStackSection_Tree_PutsAGapAboveEveryBranch()
    {
        List<PullRequestInfo> prs =
        [
            Linked(1, "main", "base"),
            Linked(11, "base", "a1"),
            Linked(12, "a1", "a2"),
            Linked(20, "base", "b1"),
        ];
        PollingService.ApplyStackRelations(prs);

        var ordered = MainViewModel.OrderStackSection(prs.Select(p => PrItemViewModel.From(p)).ToList());

        Assert.Equal([1, 11, 12, 20], ordered.Select(i => i.Number));
        Assert.Equal([2d, 8d, 2d, 8d], ordered.Select(i => i.StackIndentMargin.Top));
    }

    private static PullRequestInfo Linked(int number, string baseRef, string headRef) => new()
    {
        Number = number,
        Title = $"PR {number}",
        Url = $"https://github.com/org/repo/pull/{number}",
        Repository = "org/repo",
        Author = "someone",
        BaseRefName = baseRef,
        HeadRefName = headRef,
    };

    private static PullRequestInfo Pr(int number, int depth, string author, CIState state = CIState.Unknown, bool isDraft = false) => new()
    {
        Number = number,
        Title = $"PR {number}",
        Url = $"https://github.com/org/repo/pull/{number}",
        Repository = "org/repo",
        Author = author,
        CIState = state,
        IsDraft = isDraft,
        StackDepth = depth,
        StackOrder = depth,
        StackSize = 3,
        StackChainLength = 3,
        StackRootKey = "org/repo#41",
    };

    private static PrItemViewModel Item(int number, string root, int depth) =>
        PrItemViewModel.From(new PullRequestInfo
        {
            Number = number,
            Title = $"PR {number}",
            Url = $"https://github.com/org/repo/pull/{number}",
            Repository = "org/repo",
            Author = "someone",
            StackDepth = depth,
            StackOrder = depth,
            StackSize = 3,
            StackRootKey = root,
        });
}
