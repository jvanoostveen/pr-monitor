using PrMonitor.Models;
using PrMonitor.ViewModels;
using Xunit;

namespace PrMonitor.Tests.ViewModels;

public class ChecksViewModelTests
{
    private static CheckRunInfo Check(CheckRunState state, string name = "job") =>
        new() { Name = name, State = state };

    [Fact]
    public void BuildSummary_NoChecks_ReportsNoChecks()
    {
        var (label, state, count) = ChecksViewModel.BuildSummary([]);

        Assert.Equal("NO CHECKS", label);
        Assert.Equal(CIState.Unknown, state);
        Assert.Equal("", count);
    }

    [Fact]
    public void BuildSummary_AllSucceeded_ReportsAllPassed()
    {
        var (label, state, count) = ChecksViewModel.BuildSummary(
            [Check(CheckRunState.Success, "a"), Check(CheckRunState.Success, "b")]);

        Assert.Equal("ALL CHECKS PASSED", label);
        Assert.Equal(CIState.Success, state);
        Assert.Equal("2/2", count);
    }

    [Fact]
    public void BuildSummary_SkippedChecks_AreExcludedFromTheCounter()
    {
        var (label, _, count) = ChecksViewModel.BuildSummary(
            [Check(CheckRunState.Success, "a"), Check(CheckRunState.Skipped, "b")]);

        Assert.Equal("ALL CHECKS PASSED", label);
        Assert.Equal("1/1", count);
    }

    [Fact]
    public void BuildSummary_RunningCheck_ReportsRunningAndCountsFinishedOnes()
    {
        var (label, state, count) = ChecksViewModel.BuildSummary(
        [
            Check(CheckRunState.Success, "a"),
            Check(CheckRunState.Success, "b"),
            Check(CheckRunState.Success, "c"),
            Check(CheckRunState.Running, "d"),
            Check(CheckRunState.Skipped, "e"),
        ]);

        Assert.Equal("CHECKS RUNNING", label);
        Assert.Equal(CIState.Pending, state);
        Assert.Equal("3/4", count);
    }

    [Fact]
    public void BuildSummary_FailureOutranksRunning()
    {
        var (label, state, _) = ChecksViewModel.BuildSummary(
            [Check(CheckRunState.Failure, "a"), Check(CheckRunState.Running, "b")]);

        Assert.Equal("1 CHECK FAILED", label);
        Assert.Equal(CIState.Failure, state);
    }

    [Fact]
    public void BuildSummary_MultipleFailures_ArePluralized()
    {
        var (label, _, _) = ChecksViewModel.BuildSummary(
            [Check(CheckRunState.Failure, "a"), Check(CheckRunState.Cancelled, "b")]);

        Assert.Equal("2 CHECKS FAILED", label);
    }

    [Fact]
    public void BuildSummary_OnlyNeutralResults_ReportsCompleted()
    {
        var (label, state, _) = ChecksViewModel.BuildSummary(
            [Check(CheckRunState.Success, "a"), Check(CheckRunState.Neutral, "b")]);

        Assert.Equal("CHECKS COMPLETED", label);
        Assert.Equal(CIState.Unknown, state);
    }

    [Theory]
    [InlineData("octocat/hello-world", "octocat", "hello-world")]
    [InlineData("a/b", "a", "b")]
    public void SplitRepository_SplitsOwnerAndRepo(string input, string owner, string repo)
    {
        Assert.Equal((owner, repo), ChecksViewModel.SplitRepository(input));
    }

    [Theory]
    [InlineData("")]
    [InlineData("no-slash")]
    [InlineData("too/many/parts")]
    [InlineData("/repo")]
    [InlineData("owner/")]
    public void SplitRepository_MalformedInput_ReturnsNulls(string input)
    {
        Assert.Equal((null, null), ChecksViewModel.SplitRepository(input));
    }

    [Theory]
    [InlineData(0, "0s")]
    [InlineData(22, "22s")]
    [InlineData(59, "59s")]
    [InlineData(84, "1m 24s")]
    [InlineData(1284, "21m 24s")]
    [InlineData(3900, "1h 05m")]
    public void FormatDuration_UsesCompactUnits(int seconds, string expected)
    {
        Assert.Equal(expected, CheckItemViewModel.FormatDuration(TimeSpan.FromSeconds(seconds)));
    }

    [Fact]
    public void FormatDuration_NegativeSpan_ClampsToZero()
    {
        Assert.Equal("0s", CheckItemViewModel.FormatDuration(TimeSpan.FromSeconds(-5)));
    }

    [Fact]
    public void DurationText_SkippedAndQueuedChecks_ShowTheirStateInsteadOfATime()
    {
        Assert.Equal("skipped", new CheckItemViewModel(Check(CheckRunState.Skipped)).DurationText);
        Assert.Equal("queued", new CheckItemViewModel(Check(CheckRunState.Queued)).DurationText);
    }

    [Fact]
    public void DurationText_RunningCheckWithoutStartTime_ShowsRunning()
    {
        Assert.Equal("running…", new CheckItemViewModel(Check(CheckRunState.Running)).DurationText);
    }

    [Fact]
    public void DurationText_CompletedCheck_ShowsItsDuration()
    {
        var started = DateTimeOffset.UtcNow.AddMinutes(-2);
        var item = new CheckItemViewModel(new CheckRunInfo
        {
            Name = "Test",
            State = CheckRunState.Success,
            StartedAt = started,
            CompletedAt = started.AddSeconds(84),
        });

        Assert.Equal("1m 24s", item.DurationText);
    }

    [Fact]
    public void HasUrl_IsFalseWhenGitHubReturnedNoLink()
    {
        Assert.False(new CheckItemViewModel(Check(CheckRunState.Success)).HasUrl);
        Assert.True(new CheckItemViewModel(new CheckRunInfo
        {
            Name = "Test",
            Url = "https://github.com/o/r/actions/runs/1/job/2",
        }).HasUrl);
    }
}
