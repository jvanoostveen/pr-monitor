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
    [InlineData(CIState.Success, false, CIState.Failure)] // conflict overrides CI
    [InlineData(CIState.Success, true,  CIState.Failure)] // conflict overrides draft-grey too
    public void EffectiveCIState_WithConflicts_AlwaysFailure(
        CIState ciState, bool isDraft, CIState expected)
    {
        var vm = MakeVm(ciState: ciState, isDraft: isDraft, hasConflicts: true);
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
        Assert.Contains("Reviewers: alice (Pending), bob (Pending)", vm.PrTooltip);
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

    [Fact]
    public void PrTooltip_WithConflicts_IncludesMergeConflictsAndPreservesRealCIState()
    {
        var vm = MakeVm(ciState: CIState.Success, hasConflicts: true);
        Assert.Contains("Merge conflicts", vm.PrTooltip);
        Assert.Contains("CI: Success", vm.PrTooltip);
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

    [Fact]
    public void IsOwnPr_IsDraftSectionPr_ReturnsTrue()
    {
        var vm = MakeVm(isDraftSection: true);
        Assert.True(vm.IsOwnPr);
    }

    [Fact]
    public void ShowNoReviewerWarning_IsDraftSectionPr_TrueWhenNoReviewer()
    {
        var vm = MakeVm(isDraftSection: true, reviewerLogins: []);
        Assert.True(vm.ShowNoReviewerWarning);
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

    // ── Reviewer-state icons ────────────────────────────────────────

    [Fact]
    public void ShowChangesRequestedIcon_TrueWhenOwnPrAndReviewerRequestedChanges()
    {
        var vm = MakeVm(isMyPr: true, reviewerLogins: ["alice"],
            reviewerStates: new Dictionary<string, ReviewState> { ["alice"] = ReviewState.ChangesRequested });
        Assert.True(vm.ShowChangesRequestedIcon);
    }

    [Fact]
    public void ShowChangesRequestedIcon_FalseWhenNotOwnPr()
    {
        var vm = MakeVm(isMyPr: false, reviewerLogins: ["alice"],
            reviewerStates: new Dictionary<string, ReviewState> { ["alice"] = ReviewState.ChangesRequested });
        Assert.False(vm.ShowChangesRequestedIcon);
    }

    [Fact]
    public void ShowChangesRequestedIcon_FalseWhenUnresolvedCommentsTakePriority()
    {
        var vm = MakeVm(isMyPr: true, reviewerLogins: ["alice"], unresolvedComments: 1,
            reviewerStates: new Dictionary<string, ReviewState> { ["alice"] = ReviewState.ChangesRequested });
        Assert.False(vm.ShowChangesRequestedIcon);
    }

    [Fact]
    public void ShowReviewPendingIcon_TrueWhenOwnPrAndAllReviewersPending()
    {
        var vm = MakeVm(isMyPr: true, reviewerLogins: ["alice", "bob"]);
        Assert.True(vm.ShowReviewPendingIcon);
    }

    [Fact]
    public void ShowReviewPendingIcon_FalseWhenAnyReviewerResponded()
    {
        var vm = MakeVm(isMyPr: true, reviewerLogins: ["alice", "bob"],
            reviewerStates: new Dictionary<string, ReviewState> { ["alice"] = ReviewState.Commented });
        Assert.False(vm.ShowReviewPendingIcon);
    }

    [Fact]
    public void ShowReviewPendingIcon_FalseWhenChangesRequestedTakesPriority()
    {
        var vm = MakeVm(isMyPr: true, reviewerLogins: ["alice"],
            reviewerStates: new Dictionary<string, ReviewState> { ["alice"] = ReviewState.ChangesRequested });
        Assert.False(vm.ShowReviewPendingIcon);
    }

    [Fact]
    public void ShowCommentedIcon_TrueWhenReviewerCommentedWithoutApprovalOrChangesRequested()
    {
        var vm = MakeVm(isMyPr: true, reviewerLogins: ["alice"],
            reviewerStates: new Dictionary<string, ReviewState> { ["alice"] = ReviewState.Commented });
        Assert.True(vm.ShowCommentedIcon);
    }

    [Fact]
    public void ShowCommentedIcon_FalseWhenApproved()
    {
        var vm = MakeVm(isMyPr: true, isApproved: true, reviewerLogins: ["alice"],
            reviewerStates: new Dictionary<string, ReviewState> { ["alice"] = ReviewState.Commented });
        Assert.False(vm.ShowCommentedIcon);
    }

    [Fact]
    public void ShowCommentedIcon_FalseWhenChangesRequestedTakesPriority()
    {
        var vm = MakeVm(isMyPr: true, reviewerLogins: ["alice", "bob"],
            reviewerStates: new Dictionary<string, ReviewState>
            {
                ["alice"] = ReviewState.Commented,
                ["bob"] = ReviewState.ChangesRequested,
            });
        Assert.False(vm.ShowCommentedIcon);
    }

    // ── Team reviewers (CODEOWNERS) ─────────────────────────────────

    [Fact]
    public void HasNonCopilotReviewer_TeamOnly_FalseByDefault()
    {
        var vm = MakeVm(isMyPr: true, reviewerLogins: ["platform-team"], teamReviewerSlugs: ["platform-team"]);
        Assert.False(vm.HasNonCopilotReviewer);
        Assert.True(vm.ShowNoReviewerWarning);
    }

    [Fact]
    public void HasNonCopilotReviewer_TeamOnly_TrueWhenTeamCountsAsReviewer()
    {
        var vm = MakeVm(isMyPr: true, reviewerLogins: ["platform-team"], teamReviewerSlugs: ["platform-team"],
            teamReviewCountsAsReviewer: true);
        Assert.True(vm.HasNonCopilotReviewer);
        Assert.False(vm.ShowNoReviewerWarning);
    }

    [Fact]
    public void HasNonCopilotReviewer_TeamPlusIndividual_TrueRegardlessOfSetting()
    {
        var vm = MakeVm(isMyPr: true, reviewerLogins: ["platform-team", "alice"], teamReviewerSlugs: ["platform-team"]);
        Assert.True(vm.HasNonCopilotReviewer);
        Assert.Equal(["alice"], vm.EffectiveReviewerLogins);
    }

    [Fact]
    public void PrTooltip_OwnPrTeamOnly_MentionsTeam()
    {
        var vm = MakeVm(isMyPr: true, reviewerLogins: ["platform-team"], teamReviewerSlugs: ["platform-team"]);
        Assert.Contains("No individual reviewer assigned (team: platform-team)", vm.PrTooltip);
    }

    // ── Label chips & priority ────────────────────────────────────────

    private static readonly LabelRule PrioRule = LabelRule.DefaultPriority();

    private static PrItemViewModel FromLabels(IReadOnlyList<string> labels, IReadOnlyList<LabelRule> rules,
        CIState ci = CIState.Success, bool hasConflicts = false, bool isDraft = false) =>
        PrItemViewModel.From(new PullRequestInfo
        {
            Number = 1,
            Title = "PR",
            Url = "https://github.com/org/repo/pull/1",
            Repository = "org/repo",
            Author = "alice",
            CIState = ci,
            HasConflicts = hasConflicts,
            IsDraft = isDraft,
            Labels = labels,
        }, labelRules: rules);

    [Fact]
    public void From_PriorityLabel_MatchesCaseInsensitively_AndSetsPriority()
    {
        var vm = FromLabels(["prioriteit/high"], [PrioRule]);

        Assert.Equal(LabelPriority.High, vm.Priority);
        Assert.Equal("HIGH", Assert.Single(vm.LabelChips).Text);
    }

    [Theory]
    [InlineData(CIState.Success, false, false, "#FF3FB950")]
    [InlineData(CIState.Pending, false, false, "#FFD29922")]
    [InlineData(CIState.Success, true, false, "#FFF85149")]   // conflicts → failure colour
    [InlineData(CIState.Success, false, true, "#FF8B949E")]   // draft → muted grey
    public void From_RuleWithoutColour_FollowsEffectiveCiColour(CIState ci, bool conflicts, bool draft, string expected)
    {
        var vm = FromLabels(["Prioriteit/High"], [PrioRule], ci, conflicts, draft);

        Assert.Equal(expected, Assert.Single(vm.LabelChips).Foreground.Color.ToString());
    }

    [Fact]
    public void From_RuleWithColour_UsesThatColour()
    {
        var vm = FromLabels(["bug"], [new LabelRule { Label = "bug", Text = "BUG", Color = "#A371F7" }], CIState.Failure);

        var chip = Assert.Single(vm.LabelChips);
        Assert.Equal("#FFA371F7", chip.Foreground.Color.ToString());
        Assert.Equal(0x33, chip.Background.Color.A);
        Assert.Equal(LabelPriority.None, vm.Priority);
    }

    [Fact]
    public void From_LowPriorityLabel_SetsLow_NoAccentBar()
    {
        var vm = FromLabels(["Prioriteit/Low"], [new LabelRule { Label = "Prioriteit/Low", Text = "LOW", Priority = LabelPriority.Low }]);

        Assert.Equal(LabelPriority.Low, vm.Priority);
        Assert.False(vm.IsHighPriority);
        Assert.Equal("LOW", Assert.Single(vm.LabelChips).Text);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void From_HighAndLowBothMatch_HighWinsRegardlessOfRuleOrder(bool lowFirst)
    {
        var low = new LabelRule { Label = "low", Priority = LabelPriority.Low };
        var high = new LabelRule { Label = "high", Priority = LabelPriority.High };

        var vm = FromLabels(["low", "high"], lowFirst ? [low, high] : [high, low]);

        Assert.Equal(LabelPriority.High, vm.Priority);
        Assert.True(vm.IsHighPriority);
    }

    [Fact]
    public void From_RuleWithoutText_ShowsLabelName()
    {
        var vm = FromLabels(["bug"], [new LabelRule { Label = "bug" }]);

        Assert.Equal("bug", Assert.Single(vm.LabelChips).Text);
    }

    [Fact]
    public void From_UnmappedLabels_NoChipsButListedInTooltip()
    {
        var vm = FromLabels(["bug", "docs"], [PrioRule]);

        Assert.Empty(vm.LabelChips);
        Assert.Equal(LabelPriority.None, vm.Priority);
        Assert.Contains("Labels: bug, docs", vm.PrTooltip);
    }

    [Fact]
    public void From_ChipsFollowRuleOrder_AndSkipDuplicateText()
    {
        var rules = new[]
        {
            new LabelRule { Label = "b", Text = "B" },
            new LabelRule { Label = "a", Text = "A" },
            new LabelRule { Label = "c", Text = "a" },
        };

        var vm = FromLabels(["a", "b", "c"], rules);

        Assert.Equal(["B", "A"], vm.LabelChips.Select(c => c.Text));
    }

    [Fact]
    public void From_NoLabelRules_NoChips()
    {
        var vm = PrItemViewModel.From(new PullRequestInfo
        {
            Number = 1, Title = "PR", Url = "u", Repository = "org/repo", Author = "a", Labels = ["Prioriteit/High"],
        });

        Assert.Empty(vm.LabelChips);
        Assert.Equal(LabelPriority.None, vm.Priority);
    }

    private static PrItemViewModel MakeVm(
        CIState ciState = CIState.Unknown,
        bool isDraft = false,
        string headCommitSha = "sha123",
        bool isApproved = false,
        int unresolvedComments = 0,
        IEnumerable<string>? reviewerLogins = null,
        IReadOnlyDictionary<string, ReviewState>? reviewerStates = null,
        bool isMyPr = false,
        bool isAutoMerge = false,
        bool isHotfix = false,
        bool hasConflicts = false,
        bool isDraftSection = false,
        IEnumerable<string>? teamReviewerSlugs = null,
        bool teamReviewCountsAsReviewer = false) =>
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
            HasConflicts = hasConflicts,
            IsDraft = isDraft,
            HeadCommitSha = headCommitSha,
            IsApproved = isApproved,
            UnresolvedReviewCommentCount = unresolvedComments,
            ReviewerLogins = (reviewerLogins ?? []).ToList(),
            TeamReviewerSlugs = (teamReviewerSlugs ?? []).ToList(),
            TeamReviewCountsAsReviewer = teamReviewCountsAsReviewer,
            ReviewerStates = reviewerStates ?? new Dictionary<string, ReviewState>(),
            IsMyPr = isMyPr,
            IsAutoMergePr = isAutoMerge,
            IsHotfixPr = isHotfix,
            IsDraftSectionPr = isDraftSection,
        };
}