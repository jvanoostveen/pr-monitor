using PrMonitor.Models;
using PrMonitor.Services;
using Xunit;

namespace PrMonitor.Tests.Services;

public class PollingServiceStackTests
{
    // ── ApplyStackRelations ──────────────────────────────────────────

    [Fact]
    public void ApplyStackRelations_NoRelations_LeavesEveryPrUnstacked()
    {
        var a = MakePr(1, "main", "feature/a");
        var b = MakePr(2, "main", "feature/b");

        PollingService.ApplyStackRelations([a, b]);

        Assert.False(a.IsStacked);
        Assert.False(b.IsStacked);
        Assert.False(a.IsBlockedByStack);
        Assert.Equal(1, a.StackSize);
        Assert.Equal(0, a.StackDepth);
    }

    [Fact]
    public void ApplyStackRelations_TwoLevelStack_LinksChildToParent()
    {
        var parent = MakePr(1, "main", "ux/update-dependencies");
        var child = MakePr(2, "ux/update-dependencies", "ux/webpack-upgrade");

        PollingService.ApplyStackRelations([parent, child]);

        Assert.Equal(parent.Key, child.StackParentKey);
        Assert.Equal(1, child.StackParentNumber);
        Assert.Equal(parent.Url, child.StackParentUrl);
        Assert.Equal(1, child.StackDepth);
        Assert.Equal(2, child.StackSize);
        Assert.True(child.IsBlockedByStack);

        Assert.Null(parent.StackParentKey);
        Assert.Equal(0, parent.StackDepth);
        Assert.Equal(2, parent.StackSize);
        Assert.True(parent.IsStacked);
        Assert.False(parent.IsBlockedByStack);
    }

    [Fact]
    public void ApplyStackRelations_ThreeLevelStack_AssignsIncrementingDepths()
    {
        var a = MakePr(1, "main", "step-1");
        var b = MakePr(2, "step-1", "step-2");
        var c = MakePr(3, "step-2", "step-3");

        PollingService.ApplyStackRelations([c, a, b]);

        Assert.Equal(0, a.StackDepth);
        Assert.Equal(1, b.StackDepth);
        Assert.Equal(2, c.StackDepth);
        Assert.All(new[] { a, b, c }, p => Assert.Equal(3, p.StackSize));
        Assert.All(new[] { a, b, c }, p => Assert.Equal(a.Key, p.StackRootKey));
    }

    [Fact]
    public void ApplyStackRelations_DifferentRepositories_DoesNotLink()
    {
        var a = MakePr(1, "main", "shared-branch", repository: "org/repo-a");
        var b = MakePr(2, "shared-branch", "other", repository: "org/repo-b");

        PollingService.ApplyStackRelations([a, b]);

        Assert.False(b.IsBlockedByStack);
        Assert.Equal(1, a.StackSize);
    }

    [Fact]
    public void ApplyStackRelations_CyclicBranches_DoesNotLoopForever()
    {
        var a = MakePr(1, "branch-b", "branch-a");
        var b = MakePr(2, "branch-a", "branch-b");

        PollingService.ApplyStackRelations([a, b]);

        Assert.True(a.IsBlockedByStack);
        Assert.True(b.IsBlockedByStack);
    }

    [Fact]
    public void ApplyStackRelations_DuplicateInstancesOfSamePr_AllGetStackData()
    {
        var parent = MakePr(1, "main", "step-1");
        var childInMyPrs = MakePr(2, "step-1", "step-2");
        var childInHotfixes = MakePr(2, "step-1", "step-2");

        PollingService.ApplyStackRelations([parent, childInMyPrs, childInHotfixes]);

        Assert.Equal(1, childInMyPrs.StackDepth);
        Assert.Equal(1, childInHotfixes.StackDepth);
        Assert.Equal(2, childInHotfixes.StackSize);
    }

    [Fact]
    public void ApplyStackRelations_ResetsStaleStateFromPreviousPoll()
    {
        var pr = MakePr(1, "main", "feature/a");
        pr.StackParentKey = "org/repo#99";
        pr.StackDepth = 4;
        pr.StackSize = 5;

        PollingService.ApplyStackRelations([pr]);

        Assert.Null(pr.StackParentKey);
        Assert.Equal(0, pr.StackDepth);
        Assert.Equal(1, pr.StackSize);
    }

    // ── Stack trees (several stacks sharing a bottom PR) ──────────────

    /// <summary>
    /// base ← a1 ← a2, base ← b1 ← b2 ← b3, base ← c1: three stacks on one bottom PR,
    /// listed out of order to check the walk does not depend on input order.
    /// </summary>
    private static List<PullRequestInfo> MakeTree() =>
    [
        MakePr(22, "b2", "b3"),
        MakePr(11, "base", "a1"),
        MakePr(20, "base", "b1"),
        MakePr(30, "base", "c1"),
        MakePr(12, "a1", "a2"),
        MakePr(21, "b1", "b2"),
        MakePr(1, "main", "base"),
    ];

    [Fact]
    public void OrderByStack_Tree_ListsEachBranchContiguously()
    {
        var prs = MakeTree();
        PollingService.ApplyStackRelations(prs);

        var ordered = PollingService.OrderByStack(prs);

        Assert.Equal([1, 11, 12, 20, 21, 22, 30], ordered.Select(p => p.Number));
    }

    [Fact]
    public void ApplyStackRelations_Tree_CountsPositionPerBranch()
    {
        var prs = MakeTree();
        PollingService.ApplyStackRelations(prs);
        var byNumber = prs.ToDictionary(p => p.Number);

        Assert.All(prs, p => Assert.Equal(7, p.StackSize));
        Assert.Equal(3, byNumber[1].StackBranchCount);
        Assert.Equal((3, 3), (byNumber[12].StackDepth + 1, byNumber[12].StackChainLength));
        Assert.Equal((3, 4), (byNumber[21].StackDepth + 1, byNumber[21].StackChainLength));
        Assert.Equal((2, 2), (byNumber[30].StackDepth + 1, byNumber[30].StackChainLength));
        Assert.All(prs.Where(p => p.Number != 1), p => Assert.Equal(1, p.StackBranchCount));
    }

    [Fact]
    public void ApplyStackRelations_Tree_MarksTheFirstPrOfEachBranch()
    {
        var prs = MakeTree();
        PollingService.ApplyStackRelations(prs);

        Assert.Equal([11, 20, 30], prs.Where(p => p.IsStackBranchStart).Select(p => p.Number).Order());
    }

    [Fact]
    public void ApplyStackRelations_ForkHigherUp_ReportsBranchesOnEveryPrBelowIt()
    {
        // a ← b ← {c, d ← e}
        var a = MakePr(1, "main", "a");
        var b = MakePr(2, "a", "b");
        var c = MakePr(3, "b", "c");
        var d = MakePr(4, "b", "d");
        var e = MakePr(5, "d", "e");

        PollingService.ApplyStackRelations([a, b, c, d, e]);

        Assert.Equal(2, a.StackBranchCount);
        Assert.Equal(2, b.StackBranchCount);
        Assert.False(b.IsStackBranchStart);
        Assert.True(c.IsStackBranchStart);
        Assert.Equal(3, c.StackChainLength);
        Assert.Equal(4, e.StackChainLength);
        Assert.Equal(1, e.StackForkLevel);
    }

    [Fact]
    public void ApplyStackRelations_Tree_DuplicateInstancesGetTheSameOrder()
    {
        var prs = MakeTree();
        var duplicate = MakePr(21, "b1", "b2");

        PollingService.ApplyStackRelations([.. prs, duplicate]);

        var original = prs.Single(p => p.Number == 21);
        Assert.Equal(original.StackOrder, duplicate.StackOrder);
        Assert.Equal(original.StackChainLength, duplicate.StackChainLength);
    }

    [Fact]
    public void OrderByStack_CycleWithTail_OrdersDeterministically()
    {
        var a = MakePr(1, "branch-b", "branch-a");
        var b = MakePr(2, "branch-a", "branch-b");
        var tail = MakePr(3, "branch-b", "branch-c");

        PollingService.ApplyStackRelations([tail, b, a]);
        var ordered = PollingService.OrderByStack([tail, b, a]);

        Assert.Equal(3, ordered.Select(p => p.StackOrder).Distinct().Count());
        Assert.True(ordered.IndexOf(b) < ordered.IndexOf(tail));
    }

    // ── OrderByStack ─────────────────────────────────────────────────

    [Fact]
    public void OrderByStack_GroupsStackMembersConsecutivelyBottomFirst()
    {
        var child = MakePr(3, "step-1", "step-2");
        var unrelated = MakePr(2, "main", "feature/x");
        var parent = MakePr(1, "main", "step-1");

        var input = new List<PullRequestInfo> { child, unrelated, parent };
        PollingService.ApplyStackRelations(input);

        var ordered = PollingService.OrderByStack(input);

        Assert.Equal([parent.Key, child.Key, unrelated.Key], ordered.Select(p => p.Key));
    }

    [Fact]
    public void OrderByStack_PreservesOriginalOrderWhenNothingIsStacked()
    {
        var a = MakePr(1, "main", "a");
        var b = MakePr(2, "main", "b");
        var c = MakePr(3, "main", "c");

        var input = new List<PullRequestInfo> { c, a, b };
        PollingService.ApplyStackRelations(input);

        var ordered = PollingService.OrderByStack(input);

        Assert.Equal([c.Key, a.Key, b.Key], ordered.Select(p => p.Key));
    }

    [Fact]
    public void OrderByStack_PartialStackInSection_KeepsVisibleMembersTogether()
    {
        var parent = MakePr(1, "main", "step-1");
        var child = MakePr(2, "step-1", "step-2");
        var other = MakePr(3, "main", "other");

        PollingService.ApplyStackRelations([parent, child, other]);

        // Only the child and an unrelated PR are visible in this section
        var ordered = PollingService.OrderByStack([other, child]);

        Assert.Equal([other.Key, child.Key], ordered.Select(p => p.Key));
    }

    private static PullRequestInfo MakePr(int number, string baseRef, string headRef, string repository = "org/repo") => new()
    {
        Number = number,
        Title = $"PR {number}",
        Url = $"https://github.com/{repository}/pull/{number}",
        Repository = repository,
        Author = "someone",
        BaseRefName = baseRef,
        HeadRefName = headRef,
    };
}
