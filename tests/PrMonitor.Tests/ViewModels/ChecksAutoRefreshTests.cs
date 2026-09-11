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

        Assert.True(ChecksViewModel.ShouldAutoRefresh(result, result.RateLimitRemaining));
    }

    [Fact]
    public void ShouldAutoRefresh_EverythingFinished_StopsTheCycle()
    {
        var result = CheckFetchResult.Success(
            [Check(CheckRunState.Success, "a"), Check(CheckRunState.Failure, "b"), Check(CheckRunState.Skipped, "c")]);

        Assert.False(ChecksViewModel.ShouldAutoRefresh(result, result.RateLimitRemaining));
    }

    [Fact]
    public void ShouldAutoRefresh_NoChecksAtAll_StopsTheCycle()
    {
        Assert.False(ChecksViewModel.ShouldAutoRefresh(CheckFetchResult.Success([]), null));
    }

    [Fact]
    public void ShouldAutoRefresh_FailedCall_StopsTheCycle()
    {
        Assert.False(ChecksViewModel.ShouldAutoRefresh(CheckFetchResult.Failure(), null));
    }

    [Fact]
    public void ShouldAutoRefresh_RateLimited_StopsTheCycle()
    {
        Assert.False(ChecksViewModel.ShouldAutoRefresh(CheckFetchResult.RateLimited(DateTimeOffset.UtcNow.AddMinutes(20)), 0));
    }

    [Fact]
    public void ShouldAutoRefresh_BudgetBelowTheReserve_StopsTheCycleEvenWhileJobsRun()
    {
        var result = CheckFetchResult.Success(
            [Check(CheckRunState.Running)],
            remaining: ChecksViewModel.MinRateLimitRemaining - 1);

        Assert.False(ChecksViewModel.ShouldAutoRefresh(result, result.RateLimitRemaining));
    }

    [Fact]
    public void ShouldAutoRefresh_BudgetExactlyAtTheReserve_StillSchedules()
    {
        var result = CheckFetchResult.Success(
            [Check(CheckRunState.Running)],
            remaining: ChecksViewModel.MinRateLimitRemaining);

        Assert.True(ChecksViewModel.ShouldAutoRefresh(result, result.RateLimitRemaining));
    }

    [Fact]
    public void ShouldAutoRefresh_UnknownBudget_IsNotTreatedAsExhausted()
    {
        var result = CheckFetchResult.Success([Check(CheckRunState.Running)], remaining: null);

        Assert.True(ChecksViewModel.ShouldAutoRefresh(result, result.RateLimitRemaining));
    }

    [Fact]
    public void AutoRefreshInterval_IsThirtySeconds()
    {
        Assert.Equal(TimeSpan.FromSeconds(30), ChecksViewModel.AutoRefreshInterval);
    }

    // ── Grace period after a rerun ──────────────────────────────────────

    [Fact]
    public void ShouldAutoRefresh_JustAfterARerun_KeepsGoingEvenThoughNothingRunsYet()
    {
        // GitHub has not flipped the job to queued yet, so the reload right after the rerun
        // still shows only finished checks.
        var result = CheckFetchResult.Success([Check(CheckRunState.Failure, "Test")]);

        Assert.False(ChecksViewModel.ShouldAutoRefresh(result, null));
        Assert.True(ChecksViewModel.ShouldAutoRefresh(result, null, withinRerunGrace: true));
    }

    [Fact]
    public void ShouldAutoRefresh_RerunGrace_DoesNotOverrideAFailedCall()
    {
        Assert.False(ChecksViewModel.ShouldAutoRefresh(CheckFetchResult.Failure(), null, withinRerunGrace: true));
    }

    [Fact]
    public void ShouldAutoRefresh_RerunGrace_DoesNotOverrideARateLimit()
    {
        var limited = CheckFetchResult.RateLimited(DateTimeOffset.UtcNow.AddMinutes(10));
        Assert.False(ChecksViewModel.ShouldAutoRefresh(limited, 0, withinRerunGrace: true));

        // Nor a budget that has dropped below the reserve.
        var thin = CheckFetchResult.Success(
            [Check(CheckRunState.Failure)], remaining: ChecksViewModel.MinRateLimitRemaining - 1);
        Assert.False(ChecksViewModel.ShouldAutoRefresh(thin, thin.RateLimitRemaining, withinRerunGrace: true));
    }

    [Fact]
    public void RerunGracePeriod_IsBoundedSoAFailedRerunCannotPollForever()
    {
        Assert.Equal(TimeSpan.FromMinutes(2), ChecksViewModel.RerunGracePeriod);
    }

    // ── Budget-aware pacing ─────────────────────────────────────────────

    private static readonly DateTimeOffset Now = new(2026, 9, 11, 14, 0, 0, TimeSpan.Zero);

    [Fact]
    public void ComputeInterval_HealthyBudget_UsesTheNormalInterval()
    {
        // A full-ish budget affords far more calls than 30 s pacing would ever use.
        var interval = ChecksViewModel.ComputeInterval(4885, Now.AddMinutes(30), Now);

        Assert.Equal(ChecksViewModel.AutoRefreshInterval, interval);
    }

    [Fact]
    public void ComputeInterval_UnknownBudget_UsesTheNormalInterval()
    {
        Assert.Equal(ChecksViewModel.AutoRefreshInterval, ChecksViewModel.ComputeInterval(null, Now.AddMinutes(30), Now));
        Assert.Equal(ChecksViewModel.AutoRefreshInterval, ChecksViewModel.ComputeInterval(4000, null, Now));
    }

    [Fact]
    public void ComputeInterval_ThinBudget_SpreadsTheAllowedShareOverTheRestOfTheWindow()
    {
        // 200 points, 20% share = 40 calls, over 30 minutes = one call per 45 s.
        var interval = ChecksViewModel.ComputeInterval(200, Now.AddMinutes(30), Now);

        Assert.Equal(TimeSpan.FromSeconds(45), interval);
    }

    [Fact]
    public void ComputeInterval_SlowsDownFurtherAsTheBudgetShrinks()
    {
        var roomy = ChecksViewModel.ComputeInterval(400, Now.AddMinutes(50), Now);
        var tight = ChecksViewModel.ComputeInterval(150, Now.AddMinutes(50), Now);

        Assert.True(tight > roomy, $"expected a thinner budget to pace slower, got {tight} vs {roomy}");
    }

    [Fact]
    public void ComputeInterval_NeverExceedsTheCeiling()
    {
        var interval = ChecksViewModel.ComputeInterval(1, Now.AddHours(1), Now);

        Assert.Equal(ChecksViewModel.MaxAutoRefreshInterval, interval);
    }

    [Fact]
    public void ComputeInterval_NeverGoesBelowTheNormalInterval()
    {
        // Even an enormous budget must not make the panel poll faster than its base rate.
        var interval = ChecksViewModel.ComputeInterval(int.MaxValue, Now.AddSeconds(1), Now);

        Assert.Equal(ChecksViewModel.AutoRefreshInterval, interval);
    }

    [Fact]
    public void ComputeInterval_WindowAlreadyReset_FallsBackToTheNormalInterval()
    {
        // The budget replenishes immediately, so there is nothing left to pace against.
        var interval = ChecksViewModel.ComputeInterval(5, Now.AddSeconds(-1), Now);

        Assert.Equal(ChecksViewModel.AutoRefreshInterval, interval);
    }

    [Theory]
    [InlineData(30, "30s")]
    [InlineData(45, "45s")]
    [InlineData(60, "1m")]
    [InlineData(90, "1m 30s")]
    [InlineData(300, "5m")]
    public void FormatInterval_IsCompact(int seconds, string expected)
    {
        Assert.Equal(expected, ChecksViewModel.FormatInterval(TimeSpan.FromSeconds(seconds)));
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
