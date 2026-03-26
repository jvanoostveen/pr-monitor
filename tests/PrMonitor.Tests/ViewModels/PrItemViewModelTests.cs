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

    private static PrItemViewModel MakeVm(
        CIState ciState = CIState.Unknown,
        bool isDraft = false,
        string headCommitSha = "sha123",
        bool isApproved = false,
        int unresolvedComments = 0) =>
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
        };
}