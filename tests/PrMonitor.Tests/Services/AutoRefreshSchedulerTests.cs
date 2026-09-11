using PrMonitor.Services;
using Xunit;

namespace PrMonitor.Tests.Services;

/// <summary>
/// The scheduler is what guarantees the checks panel never leaves a timer running behind it,
/// so these tests drive the real delay with a short interval instead of mocking it away.
/// </summary>
public class AutoRefreshSchedulerTests
{
    private static readonly TimeSpan Tick = TimeSpan.FromMilliseconds(40);

    /// <summary>Generously longer than <see cref="Tick"/>, so a fired tick has time to be observed.</summary>
    private static readonly TimeSpan WellPastTick = TimeSpan.FromMilliseconds(400);

    private static AutoRefreshScheduler Scheduler(Action onTick) =>
        new(() => { onTick(); return Task.CompletedTask; }, DiagnosticsLogger.Null);

    [Fact]
    public async Task Schedule_FiresTheCallbackOnce()
    {
        int ticks = 0;
        using var scheduler = Scheduler(() => Interlocked.Increment(ref ticks));

        scheduler.Schedule(Tick);
        await Task.Delay(WellPastTick);

        Assert.Equal(1, ticks);
    }

    [Fact]
    public async Task Schedule_DoesNotRepeatOnItsOwn()
    {
        int ticks = 0;
        using var scheduler = Scheduler(() => Interlocked.Increment(ref ticks));

        scheduler.Schedule(Tick);
        await Task.Delay(TimeSpan.FromMilliseconds(600));

        Assert.Equal(1, ticks);
    }

    [Fact]
    public async Task Cancel_BeforeTheTick_PreventsTheCallback()
    {
        int ticks = 0;
        using var scheduler = Scheduler(() => Interlocked.Increment(ref ticks));

        scheduler.Schedule(Tick);
        scheduler.Cancel();
        await Task.Delay(WellPastTick);

        Assert.Equal(0, ticks);
    }

    [Fact]
    public void Cancel_WithNothingScheduled_IsANoOp()
    {
        using var scheduler = Scheduler(() => { });

        scheduler.Cancel();
        scheduler.Cancel();

        Assert.False(scheduler.IsScheduled);
    }

    [Fact]
    public async Task Schedule_CalledRepeatedly_KeepsOnlyOnePendingTick()
    {
        int ticks = 0;
        using var scheduler = Scheduler(() => Interlocked.Increment(ref ticks));

        for (int i = 0; i < 5; i++)
            scheduler.Schedule(Tick);

        await Task.Delay(WellPastTick);

        Assert.Equal(1, ticks);
    }

    [Fact]
    public void IsScheduled_ReflectsWhetherATickIsPending()
    {
        using var scheduler = Scheduler(() => { });

        Assert.False(scheduler.IsScheduled);

        scheduler.Schedule(Tick);
        Assert.True(scheduler.IsScheduled);

        scheduler.Cancel();
        Assert.False(scheduler.IsScheduled);
    }

    [Fact]
    public async Task IsScheduled_AfterTheTickFired_IsFalseAgain()
    {
        using var scheduler = Scheduler(() => { });

        scheduler.Schedule(Tick);
        await Task.Delay(WellPastTick);

        Assert.False(scheduler.IsScheduled);
    }

    [Fact]
    public async Task Cancel_FromInsideTheCallback_IsSafeAndStopsTheCycle()
    {
        int ticks = 0;
        AutoRefreshScheduler? scheduler = null;
        scheduler = new AutoRefreshScheduler(
            () =>
            {
                Interlocked.Increment(ref ticks);
                // Mirrors RefreshAsync, which cancels any pending tick before reloading.
                scheduler!.Cancel();
                return Task.CompletedTask;
            },
            DiagnosticsLogger.Null);

        using (scheduler)
        {
            scheduler.Schedule(Tick);
            await Task.Delay(WellPastTick);
        }

        Assert.Equal(1, ticks);
    }

    [Fact]
    public async Task Schedule_FromInsideTheCallback_ContinuesTheCycle()
    {
        int ticks = 0;
        AutoRefreshScheduler? scheduler = null;
        scheduler = new AutoRefreshScheduler(
            () =>
            {
                // Stop after a few rounds so the test cannot spin forever.
                if (Interlocked.Increment(ref ticks) < 3)
                    scheduler!.Schedule(Tick);
                return Task.CompletedTask;
            },
            DiagnosticsLogger.Null);

        using (scheduler)
        {
            scheduler.Schedule(Tick);
            await Task.Delay(TimeSpan.FromMilliseconds(800));
        }

        Assert.Equal(3, ticks);
    }

    [Fact]
    public async Task Dispose_PreventsAPendingTickFromFiring()
    {
        int ticks = 0;
        var scheduler = Scheduler(() => Interlocked.Increment(ref ticks));

        scheduler.Schedule(Tick);
        scheduler.Dispose();
        await Task.Delay(WellPastTick);

        Assert.Equal(0, ticks);
    }

    [Fact]
    public async Task Schedule_AfterDispose_DoesNothing()
    {
        int ticks = 0;
        var scheduler = Scheduler(() => Interlocked.Increment(ref ticks));

        scheduler.Dispose();
        scheduler.Schedule(Tick);
        await Task.Delay(WellPastTick);

        Assert.False(scheduler.IsScheduled);
        Assert.Equal(0, ticks);
    }

    [Fact]
    public async Task ThrowingCallback_IsSwallowedInsteadOfCrashingTheProcess()
    {
        using var scheduler = new AutoRefreshScheduler(
            () => throw new InvalidOperationException("boom"),
            DiagnosticsLogger.Null);

        scheduler.Schedule(Tick);
        await Task.Delay(WellPastTick);

        // Reaching this point without an unobserved-exception crash is the assertion.
        Assert.False(scheduler.IsScheduled);
    }
}
