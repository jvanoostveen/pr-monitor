using PrMonitor.Models;
using PrMonitor.ViewModels;
using Xunit;

namespace PrMonitor.Tests.ViewModels;

public class PrItemViewModelTests
{
    [Fact]
    public void FormatTimeAgo_WithinOneMinute_ReturnsJustNow()
    {
        var result = PrItemViewModel.FormatTimeAgo(DateTimeOffset.Now.AddSeconds(-30));
        Assert.Equal("just now", result);
    }

    [Fact]
    public void FormatTimeAgo_45Minutes_ReturnsMinutesAgo()
    {
        var result = PrItemViewModel.FormatTimeAgo(DateTimeOffset.Now.AddMinutes(-45));
        Assert.Equal("45m ago", result);
    }

    [Fact]
    public void FormatTimeAgo_5Hours_ReturnsHoursAgo()
    {
        var result = PrItemViewModel.FormatTimeAgo(DateTimeOffset.Now.AddHours(-5));
        Assert.Equal("5h ago", result);
    }

    [Fact]
    public void FormatTimeAgo_3Days_ReturnsDaysAgo()
    {
        var result = PrItemViewModel.FormatTimeAgo(DateTimeOffset.Now.AddDays(-3));
        Assert.Equal("3d ago", result);
    }

    [Fact]
    public void FormatTimeAgo_45Days_ReturnsFormattedMonthDay()
    {
        var date = DateTimeOffset.Now.AddDays(-45);
        var result = PrItemViewModel.FormatTimeAgo(date);
        Assert.Equal(date.ToString("MMM dd"), result);
    }

    [Theory]
    [InlineData(CIState.Failure, true,  CIState.Unknown)]
    [InlineData(CIState.Success, true,  CIState.Unknown)]
    [InlineData(CIState.Pending, true,  CIState.Unknown)]
    [InlineData(CIState.Failure, false, CIState.Failure)]
    [InlineData(CIState.Success, false, CIState.Success)]
    [InlineData(CIState.Pending, false, CIState.Pending)]
    public void EffectiveCIState_DraftAlwaysUnknown_NonDraftUsesActual(
        CIState ciState, bool isDraft, CIState expected)
    {
        var vm = MakeVm(ciState: ciState, isDraft: isDraft);
        Assert.Equal(expected, vm.EffectiveCIState);
    }

    [Theory]
    [InlineData(CIState.Failure, false, "sha123", true)]
    [InlineData(CIState.Success, false, "sha123", false)]
    [InlineData(CIState.Failure, true,  "sha123", false)]
    [InlineData(CIState.Failure, false, "",       false)]
    public void CanRerunFailedJobs_RespectedConditions(
        CIState ci, bool isDraft, string sha, bool expected)
    {
        var vm = MakeVm(ciState: ci, isDraft: isDraft, headCommitSha: sha);
        Assert.Equal(expected, vm.CanRerunFailedJobs);
    }

    [Theory]
    [InlineData(true,  false)]
    [InlineData(false, true)]
    public void CanRequestCopilotReview_OnlyForNonDraftPRs(bool isDraft, bool expected)
    {
        var vm = MakeVm(isDraft: isDraft);
        Assert.Equal(expected, vm.CanRequestCopilotReview);
    }

    [Theory]
    [InlineData(false, false, false, false)]
    [InlineData(true,  false, false, true)]
    [InlineData(false, true,  false, true)]
    [InlineData(false, false, true,  true)]
    [InlineData(true,  true,  false, true)]
    public void IsOwnPr_TrueWhenMyOrAutoMergeOrHotfix(
        bool isMyPr, bool isAutoMerge, bool isHotfix, bool expected)
    {
        var vm = MakeVm(isMyPr: isMyPr, isAutoMerge: isAutoMerge, isHotfix: isHotfix);
        Assert.Equal(expected, vm.IsOwnPr);
    }

    [Theory]
    [InlineData(new string[0],                   false)]
    [InlineData(new[] { "alice" },               true)]
    [InlineData(new[] { "alice", "bob" },         true)]
    public void HasNonCopilotReviewer_BasedOnReviewerLoginsCount(
        string[] logins, bool expected)
    {
        var vm = MakeVm(reviewerLogins: logins);
        Assert.Equal(expected, vm.HasNonCopilotReviewer);
    }

    [Fact]
    public void ReviewerTooltip_NoReviewers_ReturnsNoReviewerAssigned()
    {
        var vm = MakeVm(reviewerLogins: []);
        Assert.Equal("No reviewer assigned", vm.ReviewerTooltip);
    }

    [Fact]
    public void ReviewerTooltip_WithReviewers_ReturnsCommaJoinedNames()
    {
        var vm = MakeVm(reviewerLogins: ["alice", "bob"]);
        Assert.Equal("alice, bob", vm.ReviewerTooltip);
    }

    [Theory]
    [InlineData(false, false, false, false)]
    [InlineData(true,  false, false, true)]   // IsMyPr, no reviewer
    [InlineData(false, true,  false, true)]   // IsAutoMerge, no reviewer
    [InlineData(false, false, true,  true)]   // IsHotfix, no reviewer
    [InlineData(true,  false, false, false, new[] { "alice" })]  // IsMyPr, has reviewer
    public void ShowNoReviewerWarning_TrueWhenOwnPrAndNoReviewer(
        bool isMyPr, bool isAutoMerge, bool isHotfix, bool expected,
        string[]? reviewerLogins = null)
    {
        var vm = MakeVm(isMyPr: isMyPr, isAutoMerge: isAutoMerge, isHotfix: isHotfix,
                        reviewerLogins: reviewerLogins ?? []);
        Assert.Equal(expected, vm.ShowNoReviewerWarning);
    }

    [Fact]
    public void PrTooltip_NonOwnPr_ShowsOpenedAndCIState()
    {
        var vm = MakeVm(ciState: CIState.Success);
        Assert.Contains("CI: Success", vm.PrTooltip);
        Assert.Contains("Opened:", vm.PrTooltip);
    }

    [Fact]
    public void PrTooltip_OwnPrNoReviewer_IncludesNoReviewerAssigned()
    {
        var vm = MakeVm(ciState: CIState.Pending, isMyPr: true, reviewerLogins: []);
        Assert.Contains("No reviewer assigned", vm.PrTooltip);
        Assert.Contains("CI: Pending", vm.PrTooltip);
    }

    [Fact]
    public void PrTooltip_OwnPrWithReviewer_IncludesReviewerNames()
    {
        var vm = MakeVm(ciState: CIState.Success, isMyPr: true, reviewerLogins: ["alice", "bob"]);
        Assert.Contains("Reviewers: alice, bob", vm.PrTooltip);
    }

    [Fact]
    public void PrTooltip_WithUnresolvedComments_IncludesCommentCount()
    {
        var vm = MakeVm(ciState: CIState.Success, unresolvedComments: 3);
        Assert.Contains("3 unresolved review comments", vm.PrTooltip);
    }

    [Fact]
    public void PrTooltip_Approved_IncludesApproved()
    {
        var vm = MakeVm(ciState: CIState.Success, isApproved: true);
        Assert.Contains("Approved", vm.PrTooltip);
    }

    [Theory]
    [InlineData(true,  0, true)]
    [InlineData(true,  2, false)]
    [InlineData(false, 0, false)]
    public void ShowApprovedIcon_RespectedConditions(bool isApproved, int unresolvedCount, bool expected)
    {
        var vm = MakeVm(isApproved: isApproved, unresolvedComments: unresolvedCount);
        Assert.Equal(expected, vm.ShowApprovedIcon);
    }

    [Fact]
    public void UnresolvedReviewCommentsToolTip_OneComment_UsesSingular()
    {
        var vm = MakeVm(unresolvedComments: 1);
        Assert.Equal("1 unresolved review comment", vm.UnresolvedReviewCommentsToolTip);
    }

    [Fact]
    public void UnresolvedReviewCommentsToolTip_ThreeComments_UsesPlural()
    {
        var vm = MakeVm(unresolvedComments: 3);
        Assert.Equal("3 unresolved review comments", vm.UnresolvedReviewCommentsToolTip);
    }

    // ── CanMarkAsReady ───────────────────────────────────────────────

    [Theory]
    [InlineData(true,  true,  true)]   // own pr, draft → can mark ready
    [InlineData(true,  false, false)]  // own pr, not draft → cannot mark ready
    [InlineData(false, true,  false)]  // not own pr, draft → cannot mark ready
    [InlineData(false, false, false)]  // not own pr, not draft → cannot mark ready
    public void CanMarkAsReady_DependsOnIsOwnPrAndIsDraft(bool isOwnPr, bool isDraft, bool expected)
    {
        var vm = MakeVm(isMyPr: isOwnPr, isDraft: isDraft);
        Assert.Equal(expected, vm.CanMarkAsReady);
    }

    // ── CanConvertToDraft ────────────────────────────────────────────

    [Theory]
    [InlineData(true,  false, true)]   // own pr, not draft → can convert to draft
    [InlineData(true,  true,  false)]  // own pr, already draft → cannot convert
    [InlineData(false, false, false)]  // not own pr, not draft → cannot convert
    [InlineData(false, true,  false)]  // not own pr, draft → cannot convert
    public void CanConvertToDraft_DependsOnIsOwnPrAndNotDraft(bool isOwnPr, bool isDraft, bool expected)
    {
        var vm = MakeVm(isMyPr: isOwnPr, isDraft: isDraft);
        Assert.Equal(expected, vm.CanConvertToDraft);
    }

    private static PrItemViewModel MakeVm(
        CIState ciState = CIState.Unknown,
        bool isDraft = false,
        string headCommitSha = "sha123",
        bool isApproved = false,
        int unresolvedComments = 0,
        IEnumerable<string>? reviewerLogins = null,
        bool isMyPr = false,
        bool isAutoMerge = false,
        bool isHotfix = false) =>
        new()
        {
            Key = "org/repo#1",
            Repository = "org/repo",
            Title = "Test PR",
            Url = "https://github.com/org/repo/pull/1",
            Author = "alice",
            TimeAgo = "1d ago",
            CIIcon = "❔",
            Number = 1,
            CIState = ciState,
            IsDraft = isDraft,
            HeadCommitSha = headCommitSha,
            IsApproved = isApproved,
            UnresolvedReviewCommentCount = unresolvedComments,
            ReviewerLogins = (reviewerLogins ?? []).ToList(),
            IsMyPr = isMyPr,
            IsAutoMergePr = isAutoMerge,
            IsHotfixPr = isHotfix,
        };
}