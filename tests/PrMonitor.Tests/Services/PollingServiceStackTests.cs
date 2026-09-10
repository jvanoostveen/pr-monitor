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
