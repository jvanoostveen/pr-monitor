using PrMonitor.Models;
using PrMonitor.Services;
using PrMonitor.Settings;
using Xunit;

namespace PrMonitor.Tests.Services;

public class PollingServiceWithholdTests
{
    private static PollingService CreateService() =>
        new(new GitHubService(DiagnosticsLogger.Null), new AppSettings(), DiagnosticsLogger.Null);

    private static PullRequestInfo PR(int number = 1) => new()
    {
        Number = number,
        Title = "Test",
        Url = "https://github.com/test",
        Repository = "org/repo",
        Author = "alice",
    };

    [Fact]
    public void EmptiedSections_NothingEmptied_ReturnsEmptyList()
    {
        var previous = new PollSnapshot { MyPrs = [PR()] };
        var current = new PollSnapshot { MyPrs = [PR()] };

        Assert.Empty(PollingService.EmptiedSections(previous, current));
    }

    [Fact]
    public void EmptiedSections_SectionWentToZero_IsReported()
    {
        var previous = new PollSnapshot { MyPrs = [PR()], ReviewRequestedPrs = [PR(2)] };
        var current = new PollSnapshot { ReviewRequestedPrs = [PR(2)] };

        Assert.Equal(["My PRs"], PollingService.EmptiedSections(previous, current));
    }

    [Fact]
    public void EmptiedSections_SectionShrankButNotToZero_IsNotReported()
    {
        var previous = new PollSnapshot { MyPrs = [PR(1), PR(2)] };
        var current = new PollSnapshot { MyPrs = [PR(1)] };

        Assert.Empty(PollingService.EmptiedSections(previous, current));
    }

    [Fact]
    public void EmptiedSections_SectionWasAlreadyEmpty_IsNotReported()
    {
        Assert.Empty(PollingService.EmptiedSections(new PollSnapshot(), new PollSnapshot()));
    }

    [Fact]
    public void EmptiedSections_AllSectionsEmptied_ReportsAll()
    {
        var previous = new PollSnapshot
        {
            AutoMergePrs = [PR()],
            MyPrs = [PR()],
            DraftPrs = [PR()],
            ReviewRequestedPrs = [PR()],
            TeamReviewRequestedPrs = [PR()],
            HotfixPrs = [PR()],
            DependabotPrs = [PR()],
        };

        Assert.Equal(7, PollingService.EmptiedSections(previous, new PollSnapshot()).Count);
    }

    [Fact]
    public void ShouldWithholdSnapshot_NoPreviousSnapshot_Publishes()
    {
        var svc = CreateService();

        Assert.False(svc.ShouldWithholdSnapshot(new PollSnapshot(), out _));
    }

    [Fact]
    public void ShouldWithholdSnapshot_FirstEmptiedSection_Withholds()
    {
        var svc = CreateService();
        svc.LatestSnapshot = new PollSnapshot { MyPrs = [PR()] };

        Assert.True(svc.ShouldWithholdSnapshot(new PollSnapshot(), out var emptied));
        Assert.Equal("My PRs", emptied);
    }

    [Fact]
    public void ShouldWithholdSnapshot_SecondConsecutiveEmptiedSection_StillWithholds()
    {
        var svc = CreateService();
        svc.LatestSnapshot = new PollSnapshot { MyPrs = [PR()] };

        Assert.True(svc.ShouldWithholdSnapshot(new PollSnapshot(), out _));
        Assert.True(svc.ShouldWithholdSnapshot(new PollSnapshot(), out _));
    }

    [Fact]
    public void ShouldWithholdSnapshot_ThirdConsecutiveEmptiedSection_Publishes()
    {
        var svc = CreateService();
        svc.LatestSnapshot = new PollSnapshot { MyPrs = [PR()] };

        Assert.True(svc.ShouldWithholdSnapshot(new PollSnapshot(), out _));
        Assert.True(svc.ShouldWithholdSnapshot(new PollSnapshot(), out _));
        Assert.False(svc.ShouldWithholdSnapshot(new PollSnapshot(), out _));
    }

    [Fact]
    public void ShouldWithholdSnapshot_SectionRecovers_ResetsStreak()
    {
        var svc = CreateService();
        svc.LatestSnapshot = new PollSnapshot { MyPrs = [PR()] };

        Assert.True(svc.ShouldWithholdSnapshot(new PollSnapshot(), out _));
        Assert.False(svc.ShouldWithholdSnapshot(new PollSnapshot { MyPrs = [PR()] }, out _));

        // Streak was reset, so the next disappearance is withheld again.
        Assert.True(svc.ShouldWithholdSnapshot(new PollSnapshot(), out _));
    }

    [Fact]
    public void ShouldWithholdSnapshot_UnrelatedSectionChanges_Publishes()
    {
        var svc = CreateService();
        svc.LatestSnapshot = new PollSnapshot { MyPrs = [PR(1)] };

        Assert.False(svc.ShouldWithholdSnapshot(new PollSnapshot { MyPrs = [PR(1)], ReviewRequestedPrs = [PR(2)] }, out _));
    }
}
