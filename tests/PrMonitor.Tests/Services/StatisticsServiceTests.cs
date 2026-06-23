using System.IO;
using PrMonitor.Models;
using PrMonitor.Services;
using PrMonitor.Settings;
using Xunit;

namespace PrMonitor.Tests.Services;

public class StatisticsServiceTests
{
    private static PullRequestInfo PR(
        string repoAndNumber,
        string author = "alice",
        CIState ci = CIState.Unknown,
        DateTimeOffset? createdAt = null)
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
            Author = author,
            CIState = ci,
            CreatedAt = createdAt ?? default,
        };
    }

    private static PollSnapshot Snapshot(
        IReadOnlyList<PullRequestInfo>? myPrs = null,
        IReadOnlyList<PullRequestInfo>? autoMerge = null,
        IReadOnlyList<PullRequestInfo>? review = null)
        => new()
        {
            MyPrs = myPrs ?? [],
            AutoMergePrs = autoMerge ?? [],
            ReviewRequestedPrs = review ?? [],
        };

    private static (StatisticsService Service, StatisticsStore Store, string Path) CreateService()
    {
        var path = Path.Combine(Path.GetTempPath(), $"prstatsvc_{Guid.NewGuid()}.json");
        var store = StatisticsStore.LoadFrom(path);
        var settings = new AppSettings { GitHubUsername = "alice" };
        var service = new StatisticsService(store, settings, DiagnosticsLogger.Null);
        return (service, store, path);
    }

    private static void Cleanup(string path)
    {
        foreach (var p in new[] { path, path + ".bak", path + ".tmp" })
            if (File.Exists(p)) File.Delete(p);
    }

    private static DateOnly Today => DateOnly.FromDateTime(DateTime.Today);

    [Fact]
    public void FirstSnapshot_EstablishesBaseline_CountsNothing()
    {
        var (service, store, path) = CreateService();
        try
        {
            service.ProcessSnapshot(Snapshot(
                myPrs: [PR("org/repo#1", ci: CIState.Failure, createdAt: DateTimeOffset.UtcNow)]));

            Assert.Equal(0, store.Total().OwnPrsOpened);
            Assert.Equal(0, store.Total().CiFailures);
        }
        finally { Cleanup(path); }
    }

    [Fact]
    public void OwnPrOpened_NewKeyCreatedAfterStart_Counts()
    {
        var (service, store, path) = CreateService();
        try
        {
            service.ProcessSnapshot(Snapshot()); // baseline
            service.ProcessSnapshot(Snapshot(
                myPrs: [PR("org/repo#1", createdAt: DateTimeOffset.UtcNow)]));

            Assert.Equal(1, store.ForDay(Today).OwnPrsOpened);
        }
        finally { Cleanup(path); }
    }

    [Fact]
    public void OwnPrOpened_OldCreatedAt_NotCounted()
    {
        var (service, store, path) = CreateService();
        try
        {
            service.ProcessSnapshot(Snapshot()); // baseline
            service.ProcessSnapshot(Snapshot(
                myPrs: [PR("org/repo#1", createdAt: DateTimeOffset.UtcNow.AddDays(-5))]));

            Assert.Equal(0, store.ForDay(Today).OwnPrsOpened);
        }
        finally { Cleanup(path); }
    }

    [Fact]
    public void OwnPrMerged_KeyDisappears_Counts()
    {
        var (service, store, path) = CreateService();
        try
        {
            service.ProcessSnapshot(Snapshot(myPrs: [PR("org/repo#1")])); // baseline
            service.ProcessSnapshot(Snapshot()); // gone everywhere

            Assert.Equal(1, store.ForDay(Today).OwnPrsMerged);
        }
        finally { Cleanup(path); }
    }

    [Fact]
    public void OwnPrMerged_MovedBetweenSections_NotCounted()
    {
        var (service, store, path) = CreateService();
        try
        {
            service.ProcessSnapshot(Snapshot(autoMerge: [PR("org/repo#1")])); // baseline in auto-merge
            service.ProcessSnapshot(Snapshot(myPrs: [PR("org/repo#1")]));      // moved to My PRs

            Assert.Equal(0, store.ForDay(Today).OwnPrsMerged);
        }
        finally { Cleanup(path); }
    }

    [Fact]
    public void CiFailure_TransitionIntoFailure_Counts()
    {
        var (service, store, path) = CreateService();
        try
        {
            service.ProcessSnapshot(Snapshot(myPrs: [PR("org/repo#1", ci: CIState.Pending)])); // baseline
            service.ProcessSnapshot(Snapshot(myPrs: [PR("org/repo#1", ci: CIState.Failure)]));

            Assert.Equal(1, store.ForDay(Today).CiFailures);
        }
        finally { Cleanup(path); }
    }

    [Fact]
    public void CiFailure_StaysFailing_NotDoubleCounted()
    {
        var (service, store, path) = CreateService();
        try
        {
            service.ProcessSnapshot(Snapshot(myPrs: [PR("org/repo#1", ci: CIState.Failure)])); // baseline
            service.ProcessSnapshot(Snapshot(myPrs: [PR("org/repo#1", ci: CIState.Failure)]));

            Assert.Equal(0, store.ForDay(Today).CiFailures);
        }
        finally { Cleanup(path); }
    }

    [Fact]
    public void ReviewCompleted_RequestDisappears_Counts()
    {
        var (service, store, path) = CreateService();
        try
        {
            service.ProcessSnapshot(Snapshot(review: [PR("org/repo#9", author: "bob")])); // baseline
            service.ProcessSnapshot(Snapshot()); // review request gone

            Assert.Equal(1, store.ForDay(Today).ReviewsCompleted);
        }
        finally { Cleanup(path); }
    }

    [Fact]
    public void ReviewRequested_NewRequestAppears_Counts()
    {
        var (service, store, path) = CreateService();
        try
        {
            service.ProcessSnapshot(Snapshot()); // baseline (no review requests)
            service.ProcessSnapshot(Snapshot(review: [PR("org/repo#9", author: "bob")])); // new request

            Assert.Equal(1, store.ForDay(Today).ReviewsRequested);
        }
        finally { Cleanup(path); }
    }

    [Fact]
    public void ReviewRequested_TracksAuthor()
    {
        var (service, store, path) = CreateService();
        try
        {
            service.ProcessSnapshot(Snapshot()); // baseline
            service.ProcessSnapshot(Snapshot(review: [PR("org/repo#9", author: "bob"), PR("org/repo#10", author: "bob")]));
            service.ProcessSnapshot(Snapshot(review: [PR("org/repo#9", author: "bob"), PR("org/repo#10", author: "bob"), PR("org/repo#11", author: "carol")]));

            var day = store.ForDay(Today);
            Assert.Equal(3, day.ReviewsRequested);
            Assert.Equal(2, day.ReviewsRequestedByAuthor?["bob"]);
            Assert.Equal(1, day.ReviewsRequestedByAuthor?["carol"]);
        }
        finally { Cleanup(path); }
    }

    [Fact]
    public void Record_FlakyRerunAndRealFailure_Increment()
    {
        var (service, store, path) = CreateService();
        try
        {
            service.Record(StatMetric.FlakyReruns);
            service.Record(StatMetric.FlakyReruns);
            service.Record(StatMetric.RealFailures);

            Assert.Equal(2, store.ForDay(Today).FlakyReruns);
            Assert.Equal(1, store.ForDay(Today).RealFailures);
        }
        finally { Cleanup(path); }
    }

    [Fact]
    public void Changes_PersistToLoadedTempPath()
    {
        var (service, _, path) = CreateService();
        try
        {
            service.Record(StatMetric.FlakyReruns);

            // The store saved by the service must be readable from the same temp path.
            var reloaded = StatisticsStore.LoadFrom(path);
            Assert.Equal(1, reloaded.ForDay(Today).FlakyReruns);
        }
        finally { Cleanup(path); }
    }
}
