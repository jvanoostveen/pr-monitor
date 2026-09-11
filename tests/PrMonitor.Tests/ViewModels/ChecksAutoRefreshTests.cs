using PrMonitor.Models;
using PrMonitor.ViewModels;
using Xunit;

namespace PrMonitor.Tests.ViewModels;

/// <summary>
/// The auto-refresh cycle is a one-shot delay that re-decides after every load, so these tests
/// cover the decision itself: no running jobs, a failed call or a thin rate-limit budget must
/// all end the cycle rather than schedule another tick.
/// </summary>
public class ChecksAutoRefreshTests
{
    private static CheckRunInfo Check(CheckRunState state, string name = "job") =>
        new() { Name = name, State = state };

    [Theory]
    [InlineData(CheckRunState.Running)]
    [InlineData(CheckRunState.Queued)]
    public void ShouldAutoRefresh_UnfinishedJob_SchedulesAnotherTick(CheckRunState state)
    {
        var result = CheckFetchResult.Success([Check(CheckRunState.Success, "done"), Check(state, "busy")]);

        Assert.True(ChecksViewModel.ShouldAutoRefresh(result));
    }

    [Fact]
    public void ShouldAutoRefresh_EverythingFinished_StopsTheCycle()
    {
        var result = CheckFetchResult.Success(
            [Check(CheckRunState.Success, "a"), Check(CheckRunState.Failure, "b"), Check(CheckRunState.Skipped, "c")]);

        Assert.False(ChecksViewModel.ShouldAutoRefresh(result));
    }

    [Fact]
    public void ShouldAutoRefresh_NoChecksAtAll_StopsTheCycle()
    {
        Assert.False(ChecksViewModel.ShouldAutoRefresh(CheckFetchResult.Success([])));
    }

    [Fact]
    public void ShouldAutoRefresh_FailedCall_StopsTheCycle()
    {
        Assert.False(ChecksViewModel.ShouldAutoRefresh(CheckFetchResult.Failure()));
    }

    [Fact]
    public void ShouldAutoRefresh_RateLimited_StopsTheCycle()
    {
        Assert.False(ChecksViewModel.ShouldAutoRefresh(CheckFetchResult.RateLimited(DateTimeOffset.UtcNow.AddMinutes(20))));
    }

    [Fact]
    public void ShouldAutoRefresh_BudgetBelowTheReserve_StopsTheCycleEvenWhileJobsRun()
    {
        var result = CheckFetchResult.Success(
            [Check(CheckRunState.Running)],
            remaining: ChecksViewModel.MinRateLimitRemaining - 1);

        Assert.False(ChecksViewModel.ShouldAutoRefresh(result));
    }

    [Fact]
    public void ShouldAutoRefresh_BudgetExactlyAtTheReserve_StillSchedules()
    {
        var result = CheckFetchResult.Success(
            [Check(CheckRunState.Running)],
            remaining: ChecksViewModel.MinRateLimitRemaining);

        Assert.True(ChecksViewModel.ShouldAutoRefresh(result));
    }

    [Fact]
    public void ShouldAutoRefresh_UnknownBudget_IsNotTreatedAsExhausted()
    {
        var result = CheckFetchResult.Success([Check(CheckRunState.Running)], remaining: null);

        Assert.True(ChecksViewModel.ShouldAutoRefresh(result));
    }

    [Fact]
    public void AutoRefreshInterval_IsThirtySeconds()
    {
        Assert.Equal(TimeSpan.FromSeconds(30), ChecksViewModel.AutoRefreshInterval);
    }

    [Fact]
    public void DescribeFailure_RateLimited_NamesTheResetTimeAndSaysRefreshStopped()
    {
        var reset = new DateTimeOffset(2026, 9, 11, 14, 30, 0, TimeSpan.Zero);
        var message = ChecksViewModel.DescribeFailure(CheckFetchResult.RateLimited(reset));

        Assert.Contains("rate limit", message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("auto-refresh stopped", message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(reset.ToLocalTime().ToString("HH:mm"), message);
    }

    [Fact]
    public void DescribeFailure_RateLimitedWithoutResetTime_StillExplainsTheStop()
    {
        var message = ChecksViewModel.DescribeFailure(CheckFetchResult.RateLimited());

        Assert.Contains("rate limit", message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("until", message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DescribeFailure_PlainFailure_MentionsGhAuthentication()
    {
        Assert.Contains("gh", ChecksViewModel.DescribeFailure(CheckFetchResult.Failure()));
    }

    [Fact]
    public void DescribeFailure_SuccessfulFetch_HasNoMessage()
    {
        Assert.Equal("", ChecksViewModel.DescribeFailure(CheckFetchResult.Success([])));
    }
}
