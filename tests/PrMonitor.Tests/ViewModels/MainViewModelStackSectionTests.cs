using PrMonitor.Models;
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
        StackSize = 3,
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
            StackSize = 3,
            StackRootKey = root,
        });
}
