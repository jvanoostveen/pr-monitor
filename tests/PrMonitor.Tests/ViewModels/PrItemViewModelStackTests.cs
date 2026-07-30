using PrMonitor.Models;
using PrMonitor.ViewModels;
using Xunit;

namespace PrMonitor.Tests.ViewModels;

public class PrItemViewModelStackTests
{
    [Fact]
    public void EffectiveCIState_SuccessWithOpenStackParent_StaysSuccess()
    {
        var vm = Make(CIState.Success, blocked: true);
        Assert.Equal(CIState.Success, vm.EffectiveCIState);
    }

    [Fact]
    public void EffectiveCIState_PendingWithOpenStackParent_StaysPending()
    {
        var vm = Make(CIState.Pending, blocked: true);
        Assert.Equal(CIState.Pending, vm.EffectiveCIState);
    }

    [Fact]
    public void EffectiveCIState_FailureWithOpenStackParent_StaysFailure()
    {
        var vm = Make(CIState.Failure, blocked: true);
        Assert.Equal(CIState.Failure, vm.EffectiveCIState);
    }

    [Fact]
    public void EffectiveCIState_ErrorWithOpenStackParent_StaysError()
    {
        var vm = Make(CIState.Error, blocked: true);
        Assert.Equal(CIState.Error, vm.EffectiveCIState);
    }

    [Fact]
    public void EffectiveCIState_ConflictsWithOpenStackParent_StaysFailure()
    {
        var vm = Make(CIState.Success, blocked: true, hasConflicts: true);
        Assert.Equal(CIState.Failure, vm.EffectiveCIState);
    }

    [Fact]
    public void EffectiveCIState_DraftWithOpenStackParent_StaysUnknown()
    {
        var vm = Make(CIState.Success, blocked: true, isDraft: true);
        Assert.Equal(CIState.Unknown, vm.EffectiveCIState);
    }

    [Fact]
    public void EffectiveCIState_NotStacked_KeepsRawState()
    {
        var vm = Make(CIState.Success, blocked: false);
        Assert.Equal(CIState.Success, vm.EffectiveCIState);
    }

    [Fact]
    public void StackBadgeText_StackedAndEnabled_ShowsPosition()
    {
        var vm = Make(CIState.Success, blocked: true, stackDepth: 1, stackSize: 3);
        Assert.Equal(" · stack 2/3", vm.StackBadgeText);
    }

    [Fact]
    public void StackBadgeText_StackDisplayDisabled_IsEmpty()
    {
        var vm = Make(CIState.Success, blocked: true, stackDepth: 1, stackSize: 3, showStackRelations: false);
        Assert.Equal("", vm.StackBadgeText);
        Assert.Equal(0, vm.StackIndentMargin.Left);
    }

    [Fact]
    public void StackBadgeText_NotStacked_IsEmpty()
    {
        var vm = Make(CIState.Success, blocked: false, stackDepth: 0, stackSize: 1);
        Assert.Equal("", vm.StackBadgeText);
    }

    [Fact]
    public void StackIndentMargin_DeeperLevels_KeepSingleIndent()
    {
        var vm = Make(CIState.Success, blocked: true, stackDepth: 2, stackSize: 3);
        Assert.Equal(14, vm.StackIndentMargin.Left);
    }

    [Fact]
    public void StackIndentMargin_BottomOfStack_IsNotIndented()
    {
        var vm = Make(CIState.Success, blocked: false, stackDepth: 0, stackSize: 3);
        Assert.Equal(0, vm.StackIndentMargin.Left);
    }

    [Fact]
    public void PrTooltip_BlockedByStack_MentionsParent()
    {
        var vm = Make(CIState.Success, blocked: true, stackDepth: 1, stackSize: 2);
        Assert.Contains("Stack: 2 of 2 — waiting on #41", vm.PrTooltip);
    }

    [Fact]
    public void PrTooltip_BottomOfStack_MentionsBottom()
    {
        var vm = Make(CIState.Success, blocked: false, stackDepth: 0, stackSize: 2);
        Assert.Contains("bottom of the stack", vm.PrTooltip);
    }

    [Fact]
    public void CanOpenStackParent_WithoutParentUrl_IsFalse()
    {
        var vm = Make(CIState.Success, blocked: false, stackDepth: 0, stackSize: 2);
        Assert.False(vm.CanOpenStackParent);
    }

    private static PrItemViewModel Make(
        CIState ciState,
        bool blocked,
        bool hasConflicts = false,
        bool isDraft = false,
        int stackDepth = 1,
        int stackSize = 2,
        bool showStackRelations = true)
    {
        var pr = new PullRequestInfo
        {
            Number = 42,
            Title = "Some PR",
            Url = "https://github.com/org/repo/pull/42",
            Repository = "org/repo",
            Author = "someone",
            CIState = ciState,
            HasConflicts = hasConflicts,
            IsDraft = isDraft,
            StackDepth = stackDepth,
            StackSize = stackSize,
            StackRootKey = "org/repo#41",
        };

        if (blocked)
        {
            pr.StackParentKey = "org/repo#41";
            pr.StackParentNumber = 41;
            pr.StackParentUrl = "https://github.com/org/repo/pull/41";
        }

        return PrItemViewModel.From(pr, isMyPr: true, showStackRelations: showStackRelations);
    }
}
