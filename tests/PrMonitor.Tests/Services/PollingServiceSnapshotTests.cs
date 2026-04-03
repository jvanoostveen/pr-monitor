using PrMonitor.Models;
using PrMonitor.Services;
using Xunit;

namespace PrMonitor.Tests.Services;

public class PollingServiceSnapshotTests
{
    // ── FailedCICount ────────────────────────────────────────────────

    [Fact]
    public void FailedCICount_EmptyAutoMergePrs_ReturnsZero()
    {
        var snapshot = new PollSnapshot();
        Assert.Equal(0, snapshot.FailedCICount);
    }

    [Fact]
    public void FailedCICount_AllSuccess_ReturnsZero()
    {
        var snapshot = new PollSnapshot
        {
            AutoMergePrs = [MakePr(CIState.Success), MakePr(CIState.Success)],
        };
        Assert.Equal(0, snapshot.FailedCICount);
    }

    [Fact]
    public void FailedCICount_OneFailure_ReturnsOne()
    {
        var snapshot = new PollSnapshot
        {
            AutoMergePrs = [MakePr(CIState.Success), MakePr(CIState.Failure)],
        };
        Assert.Equal(1, snapshot.FailedCICount);
    }

    [Fact]
    public void FailedCICount_MultipleFailures_CountsAll()
    {
        var snapshot = new PollSnapshot
        {
            AutoMergePrs =
            [
                MakePr(CIState.Failure),
                MakePr(CIState.Failure),
                MakePr(CIState.Success),
                MakePr(CIState.Pending),
            ],
        };
        Assert.Equal(2, snapshot.FailedCICount);
    }

    [Fact]
    public void FailedCICount_OnlyCountsAutoMergePrs_NotReviewOrMyPrs()
    {
        var snapshot = new PollSnapshot
        {
            AutoMergePrs         = [MakePr(CIState.Success)],
            ReviewRequestedPrs   = [MakePr(CIState.Failure)],
            MyPrs                = [MakePr(CIState.Failure)],
        };
        Assert.Equal(0, snapshot.FailedCICount);
    }

    // ── PendingCICount ───────────────────────────────────────────────

    [Fact]
    public void PendingCICount_EmptyAutoMergePrs_ReturnsZero()
    {
        var snapshot = new PollSnapshot();
        Assert.Equal(0, snapshot.PendingCICount);
    }

    [Fact]
    public void PendingCICount_CountsPendingAndUnknown()
    {
        var snapshot = new PollSnapshot
        {
            AutoMergePrs =
            [
                MakePr(CIState.Pending),
                MakePr(CIState.Unknown),
                MakePr(CIState.Success),
                MakePr(CIState.Failure),
            ],
        };
        Assert.Equal(2, snapshot.PendingCICount);
    }

    [Fact]
    public void PendingCICount_DoesNotCountErrorOrSuccess()
    {
        var snapshot = new PollSnapshot
        {
            AutoMergePrs = [MakePr(CIState.Error), MakePr(CIState.Success)],
        };
        Assert.Equal(0, snapshot.PendingCICount);
    }

    // ── TotalCount ───────────────────────────────────────────────────

    [Fact]
    public void TotalCount_Empty_ReturnsZero()
    {
        var snapshot = new PollSnapshot();
        Assert.Equal(0, snapshot.TotalCount);
    }

    [Fact]
    public void TotalCount_SumsAutoMergeAndReviewPrs()
    {
        var snapshot = new PollSnapshot
        {
            AutoMergePrs       = [MakePr(), MakePr()],
            ReviewRequestedPrs = [MakePr()],
        };
        Assert.Equal(3, snapshot.TotalCount);
    }

    [Fact]
    public void TotalCount_DoesNotIncludeMyPrsOrTeamReview()
    {
        var snapshot = new PollSnapshot
        {
            AutoMergePrs             = [MakePr()],
            MyPrs                    = [MakePr(), MakePr()],
            TeamReviewRequestedPrs   = [MakePr()],
        };
        Assert.Equal(1, snapshot.TotalCount);
    }

    [Fact]
    public void TotalCount_DoesNotIncludeDraftPrsOrDependabotPrs()
    {
        var snapshot = new PollSnapshot
        {
            AutoMergePrs   = [MakePr()],
            DraftPrs       = [MakePr(), MakePr()],
            DependabotPrs  = [MakePr()],
        };
        Assert.Equal(1, snapshot.TotalCount);
    }

    private static PullRequestInfo MakePr(CIState ciState = CIState.Success) =>
        new()
        {
            Number     = 1,
            Title      = "PR",
            Url        = "https://github.com/org/repo/pull/1",
            Repository = "org/repo",
            Author     = "alice",
            CIState    = ciState,
        };
}
