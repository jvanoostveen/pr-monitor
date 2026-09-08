using PrMonitor.Models;
using PrMonitor.Services;
using PrMonitor.Settings;
using Xunit;

namespace PrMonitor.Tests.Services;

public class PollingServiceEmptySnapshotTests
{
    private static PollingService CreateService() =>
        new(new GitHubService(DiagnosticsLogger.Null), new AppSettings(), DiagnosticsLogger.Null);

    private static PullRequestInfo PR() => new()
    {
        Number = 1,
        Title = "Test",
        Url = "https://github.com/test",
        Repository = "org/repo",
        Author = "alice",
    };

    private static PollSnapshot NonEmpty() => new() { MyPrs = [PR()] };

    [Fact]
    public void IsEmptySnapshot_AllSectionsEmpty_ReturnsTrue()
    {
        Assert.True(PollingService.IsEmptySnapshot(new PollSnapshot()));
    }

    [Theory]
    [InlineData("automerge")]
    [InlineData("myprs")]
    [InlineData("draft")]
    [InlineData("review")]
    [InlineData("team")]
    [InlineData("hotfix")]
    [InlineData("dependabot")]
    public void IsEmptySnapshot_AnySectionPopulated_ReturnsFalse(string section)
    {
        List<PullRequestInfo> prs = [PR()];
        var snapshot = section switch
        {
            "automerge"  => new PollSnapshot { AutoMergePrs = prs },
            "myprs"      => new PollSnapshot { MyPrs = prs },
            "draft"      => new PollSnapshot { DraftPrs = prs },
            "review"     => new PollSnapshot { ReviewRequestedPrs = prs },
            "team"       => new PollSnapshot { TeamReviewRequestedPrs = prs },
            "hotfix"     => new PollSnapshot { HotfixPrs = prs },
            _            => new PollSnapshot { DependabotPrs = prs },
        };

        Assert.False(PollingService.IsEmptySnapshot(snapshot));
    }

    [Fact]
    public void ShouldWithholdEmptySnapshot_FirstEmptyAfterNonEmpty_Withholds()
    {
        var svc = CreateService();
        svc.LatestSnapshot = NonEmpty();

        Assert.True(svc.ShouldWithholdEmptySnapshot(new PollSnapshot()));
    }

    [Fact]
    public void ShouldWithholdEmptySnapshot_SecondConsecutiveEmpty_Publishes()
    {
        var svc = CreateService();
        svc.LatestSnapshot = NonEmpty();

        Assert.True(svc.ShouldWithholdEmptySnapshot(new PollSnapshot()));
        Assert.False(svc.ShouldWithholdEmptySnapshot(new PollSnapshot()));
    }

    [Fact]
    public void ShouldWithholdEmptySnapshot_NoPreviousSnapshot_Publishes()
    {
        var svc = CreateService();

        Assert.False(svc.ShouldWithholdEmptySnapshot(new PollSnapshot()));
    }

    [Fact]
    public void ShouldWithholdEmptySnapshot_PreviousAlsoEmpty_Publishes()
    {
        var svc = CreateService();
        svc.LatestSnapshot = new PollSnapshot();

        Assert.False(svc.ShouldWithholdEmptySnapshot(new PollSnapshot()));
    }

    [Fact]
    public void ShouldWithholdEmptySnapshot_NonEmptySnapshot_ResetsStreak()
    {
        var svc = CreateService();
        svc.LatestSnapshot = NonEmpty();

        Assert.True(svc.ShouldWithholdEmptySnapshot(new PollSnapshot()));
        Assert.False(svc.ShouldWithholdEmptySnapshot(NonEmpty()));

        // Streak was reset, so the next empty poll is withheld again.
        Assert.True(svc.ShouldWithholdEmptySnapshot(new PollSnapshot()));
    }
}
