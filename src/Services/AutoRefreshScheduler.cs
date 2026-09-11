namespace PrMonitor.Services;

/// <summary>
/// Runs a callback once after a delay, and at most one such tick at a time.
/// </summary>
/// <remarks>
/// Deliberately not a repeating timer: the caller decides after every tick whether another one
/// is warranted, so a scheduler can never outlive the thing it refreshes, stack up duplicate
/// ticks, or keep firing against an API that has started refusing calls. <see cref="Cancel"/>
/// is idempotent and safe to call from a tick.
/// <para>
/// The tick resumes on the synchronization context that scheduled it, so a WPF caller stays on
/// the dispatcher thread and may touch bound collections directly.
/// </para>
/// </remarks>
internal sealed class AutoRefreshScheduler : IDisposable
{
    private readonly Func<Task> _onTick;
    private readonly DiagnosticsLogger _logger;
    private CancellationTokenSource? _cts;
    private bool _disposed;

    public AutoRefreshScheduler(Func<Task> onTick, DiagnosticsLogger logger)
    {
        _onTick = onTick;
        _logger = logger;
    }

    /// <summary>Whether a tick is currently pending. False whenever nothing is scheduled.</summary>
    public bool IsScheduled => _cts is not null;

    /// <summary>
    /// Cancels any pending tick and schedules a new one after <paramref name="delay"/>.
    /// The delay is per call, so a caller can slow itself down as conditions change.
    /// Does nothing after <see cref="Dispose"/>.
    /// </summary>
    public void Schedule(TimeSpan delay)
    {
        Cancel();
        if (_disposed)
            return;

        var cts = new CancellationTokenSource();
        _cts = cts;
        _ = RunAsync(cts, delay);
    }

    /// <summary>Cancels the pending tick, if any. Safe to call repeatedly and from within a tick.</summary>
    public void Cancel()
    {
        var cts = _cts;
        _cts = null;
        if (cts is null)
            return;

        cts.Cancel();
        cts.Dispose();
    }

    private async Task RunAsync(CancellationTokenSource cts, TimeSpan delay)
    {
        try
        {
            await Task.Delay(delay, cts.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        finally
        {
            // A cancelling caller already replaced the field and disposed the source;
            // only clean up while this tick is still the current one.
            if (ReferenceEquals(_cts, cts))
            {
                _cts = null;
                cts.Dispose();
            }
        }

        try
        {
            await _onTick();
        }
        catch (Exception ex)
        {
            // A throwing tick must not take down the process as an unobserved task exception.
            _logger.Warn($"AutoRefreshScheduler: the scheduled callback threw: {ex.Message}");
        }
    }

    public void Dispose()
    {
        _disposed = true;
        Cancel();
    }
}
