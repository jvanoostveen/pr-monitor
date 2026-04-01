using PrMonitor.Models;
using PrMonitor.Services;
using PrMonitor.Settings;
using Xunit;

namespace PrMonitor.Tests.Services;

public class PollingServiceDeltaTests
{
    private static PollingService CreateService() =>
        new(new GitHubService(DiagnosticsLogger.Null), new AppSettings(), DiagnosticsLogger.Null);

    private static PullRequestInfo PR(string repoAndNumber, CIState ci = CIState.Unknown)
    {
        var idx = repoAndNumber.LastIndexOf('#');
        var repo = repoAndNumber[..idx];
        var number = int.Parse(repoAndNumber[(idx + 1)..]);
        return new PullRequestInfo
        {
            Number = number,
            Title = "Test",
            Url = "https://github.com/test",
            Repository = repo,
            Author = "alice",
            CIState = ci,
        };
    }

    [Fact]
    public void DetectAutoMergeChanges_NewPr_RaisesNewAutoMergeEvent()
    {
        var svc = CreateService();
        var events = new List<PrChangeEventArgs>();
        svc.PrChanged += (_, e) => events.Add(e);

        svc.DetectAutoMergeChanges([PR("org/repo#1")]);

        Assert.Single(events);
        Assert.Equal(PrChangeKind.NewAutoMergePr, events[0].Kind);
        Assert.Equal(1, events[0].PullRequest.Number);
    }

    [Fact]
    public void DetectAutoMergeChanges_ExistingPrUnchangedCIState_NoEvent()
    {
        var svc = CreateService();
        var pr = PR("org/repo#1", CIState.Success);
        svc._previousAutoMerge["org/repo#1"] = pr;

        var events = new List<PrChangeEventArgs>();
        svc.PrChanged += (_, e) => events.Add(e);
        svc.DetectAutoMergeChanges([pr]);

        Assert.Empty(events);
    }

    [Fact]
    public void DetectAutoMergeChanges_CIStateChanged_RaisesCIStatusChangedEvent()
    {
        var svc = CreateService();
        svc._previousAutoMerge["org/repo#1"] = PR("org/repo#1", CIState.Pending);

        var events = new List<PrChangeEventArgs>();
        svc.PrChanged += (_, e) => events.Add(e);
        svc.DetectAutoMergeChanges([PR("org/repo#1", CIState.Failure)]);

        Assert.Single(events);
        Assert.Equal(PrChangeKind.CIStatusChanged, events[0].Kind);
        Assert.Equal(CIState.Pending, events[0].PreviousCIState);
        Assert.Equal(CIState.Failure, events[0].PullRequest.CIState);
    }

    [Fact]
    public void DetectAutoMergeChanges_RemovedPr_RaisesRemovedEvent()
    {
        var svc = CreateService();
        svc._previousAutoMerge["org/repo#1"] = PR("org/repo#1");

        var events = new List<PrChangeEventArgs>();
        svc.PrChanged += (_, e) => events.Add(e);
        svc.DetectAutoMergeChanges([]);

        Assert.Single(events);
        Assert.Equal(PrChangeKind.RemovedAutoMergePr, events[0].Kind);
    }

    [Fact]
    public void DetectAutoMergeChanges_RemovedPr_StillOpenInAllMyPrs_NoEvent()
    {
        // PR had auto-merge disabled but is still open — should not fire a merged/closed notification.
        var svc = CreateService();
        svc._previousAutoMerge["org/repo#1"] = PR("org/repo#1");

        var events = new List<PrChangeEventArgs>();
        svc.PrChanged += (_, e) => events.Add(e);
        svc.DetectAutoMergeChanges([], allOpenPrKeys: ["org/repo#1"]);

        Assert.Empty(events);
    }

    [Fact]
    public void DetectAutoMergeChanges_RemovedPr_NotInAllMyPrs_RaisesRemovedEvent()
    {
        // PR is gone from all open PRs too — it was truly closed/merged.
        var svc = CreateService();
        svc._previousAutoMerge["org/repo#1"] = PR("org/repo#1");

        var events = new List<PrChangeEventArgs>();
        svc.PrChanged += (_, e) => events.Add(e);
        svc.DetectAutoMergeChanges([], allOpenPrKeys: []);

        Assert.Single(events);
        Assert.Equal(PrChangeKind.RemovedAutoMergePr, events[0].Kind);
    }

    [Fact]
    public void DetectAutoMergeChanges_AfterCall_UpdatesPreviousDict()
    {
        var svc = CreateService();
        svc.DetectAutoMergeChanges([PR("org/repo#1"), PR("org/repo#2")]);

        Assert.Equal(2, svc._previousAutoMerge.Count);
        Assert.True(svc._previousAutoMerge.ContainsKey("org/repo#1"));
        Assert.True(svc._previousAutoMerge.ContainsKey("org/repo#2"));
    }

    [Fact]
    public void DetectReviewChanges_NewPr_RaisesNewReviewEvent()
    {
        var svc = CreateService();
        var events = new List<PrChangeEventArgs>();
        svc.PrChanged += (_, e) => events.Add(e);

        svc.DetectReviewChanges([PR("org/repo#5")]);

        Assert.Single(events);
        Assert.Equal(PrChangeKind.NewReviewRequested, events[0].Kind);
    }

    [Fact]
    public void DetectReviewChanges_RemovedPr_RaisesReviewRemovedEvent()
    {
        var svc = CreateService();
        svc._previousReviews["org/repo#5"] = PR("org/repo#5");

        var events = new List<PrChangeEventArgs>();
        svc.PrChanged += (_, e) => events.Add(e);
        svc.DetectReviewChanges([]);

        Assert.Single(events);
        Assert.Equal(PrChangeKind.ReviewRequestRemoved, events[0].Kind);
    }

    [Fact]
    public void DetectMyPrsChanges_CIStateChanged_RaisesCIStatusChangedEvent()
    {
        var svc = CreateService();
        svc._previousMyPrs["org/repo#3"] = PR("org/repo#3", CIState.Pending);

        var events = new List<PrChangeEventArgs>();
        svc.PrChanged += (_, e) => events.Add(e);
        svc.DetectMyPrsChanges([PR("org/repo#3", CIState.Success)]);

        Assert.Single(events);
        Assert.Equal(PrChangeKind.CIStatusChanged, events[0].Kind);
    }

    [Fact]
    public void DetectMyPrsChanges_NewPr_DoesNotRaiseEvent()
    {
        var svc = CreateService();
        var events = new List<PrChangeEventArgs>();
        svc.PrChanged += (_, e) => events.Add(e);

        svc.DetectMyPrsChanges([PR("org/repo#3")]);

        Assert.Empty(events);
    }
}