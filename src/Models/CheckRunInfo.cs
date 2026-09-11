namespace PrMonitor.Models;

/// <summary>
/// Normalized state of a single CI check (GitHub Actions check run or legacy status context).
/// Ordering of the enum is not significant; sorting is driven by <see cref="CheckRunInfo.SortRank"/>.
/// </summary>
public enum CheckRunState
{
    /// <summary>Queued or waiting for a runner.</summary>
    Queued,

    /// <summary>Currently executing.</summary>
    Running,

    /// <summary>Completed successfully.</summary>
    Success,

    /// <summary>Completed with a failure, timeout or startup failure.</summary>
    Failure,

    /// <summary>Cancelled before completing.</summary>
    Cancelled,

    /// <summary>Skipped because a condition excluded it.</summary>
    Skipped,

    /// <summary>Completed neutrally or requires manual action.</summary>
    Neutral,

    /// <summary>State could not be determined.</summary>
    Unknown,
}

/// <summary>
/// One CI check on a pull request's head commit, with enough detail to show status,
/// duration and a direct link to the job on GitHub.
/// </summary>
public sealed class CheckRunInfo
{
    /// <summary>Job/check name, e.g. "Compile" or "Test (linux)".</summary>
    public required string Name { get; init; }

    /// <summary>
    /// Workflow the check belongs to ("Components", "Main PR"), or empty for legacy status
    /// contexts. Job names alone are routinely ambiguous — two workflows both having a "Test"
    /// job is the norm — so this is what makes a row identifiable.
    /// </summary>
    public string WorkflowName { get; init; } = "";

    /// <summary>Event that triggered the run ("pull_request", "pull_request_review"), when known.</summary>
    public string Event { get; init; } = "";

    /// <summary>Normalized state used for icon, color and sorting.</summary>
    public CheckRunState State { get; init; } = CheckRunState.Unknown;

    /// <summary>Direct link to the job log (check run) or the external status target.</summary>
    public string Url { get; init; } = "";

    /// <summary>When the check started, when GitHub reported it.</summary>
    public DateTimeOffset? StartedAt { get; init; }

    /// <summary>When the check completed; null while queued or running.</summary>
    public DateTimeOffset? CompletedAt { get; init; }

    /// <summary>Workflow run ID the check belongs to, or 0 for legacy status contexts.</summary>
    public long WorkflowRunId { get; init; }

    /// <summary>
    /// GitHub Actions job ID, used to rerun this single job. For an Actions check run this is
    /// the check run's own <c>databaseId</c> — the same number as the <c>/job/&lt;id&gt;</c>
    /// segment of <see cref="Url"/>. Zero for status contexts and third-party check runs.
    /// </summary>
    public long JobId { get; init; }

    /// <summary>Whether this check can be rerun on its own: a failed GitHub Actions job.</summary>
    public bool CanRerun => IsFailure && JobId > 0 && WorkflowRunId > 0;

    /// <summary>True while the check has not reached a terminal state.</summary>
    public bool IsInProgress => State is CheckRunState.Queued or CheckRunState.Running;

    /// <summary>True when the check counts as a failure the user has to act on.</summary>
    public bool IsFailure => State is CheckRunState.Failure or CheckRunState.Cancelled;

    /// <summary>True when the check finished without running any work.</summary>
    public bool IsSkipped => State is CheckRunState.Skipped;

    /// <summary>
    /// Wall-clock duration: completed checks use their real span, running checks the time
    /// elapsed so far, and checks without a start time report nothing.
    /// </summary>
    public TimeSpan? Duration =>
        StartedAt is not { } started ? null
        : CompletedAt is { } completed ? completed - started
        : IsInProgress ? DateTimeOffset.UtcNow - started
        : null;

    /// <summary>
    /// Display order: failures first (they are what the user opened the panel for), then
    /// running, queued, succeeded and finally skipped/neutral noise.
    /// </summary>
    public int SortRank => State switch
    {
        CheckRunState.Failure => 0,
        CheckRunState.Cancelled => 1,
        CheckRunState.Running => 2,
        CheckRunState.Queued => 3,
        CheckRunState.Success => 4,
        CheckRunState.Neutral => 5,
        CheckRunState.Skipped => 6,
        _ => 7,
    };

    /// <summary>Maps a GitHub CheckRun status/conclusion pair onto <see cref="CheckRunState"/>.</summary>
    public static CheckRunState FromCheckRun(string? status, string? conclusion)
    {
        // A conclusion is only meaningful once the run reached COMPLETED; GitHub leaves it null before that.
        if (!string.Equals(status, "COMPLETED", StringComparison.OrdinalIgnoreCase))
        {
            return status?.ToUpperInvariant() switch
            {
                "IN_PROGRESS" => CheckRunState.Running,
                "QUEUED" or "WAITING" or "PENDING" or "REQUESTED" => CheckRunState.Queued,
                _ => CheckRunState.Unknown,
            };
        }

        return conclusion?.ToUpperInvariant() switch
        {
            "SUCCESS" => CheckRunState.Success,
            "FAILURE" or "TIMED_OUT" or "STARTUP_FAILURE" => CheckRunState.Failure,
            "CANCELLED" => CheckRunState.Cancelled,
            "SKIPPED" => CheckRunState.Skipped,
            "NEUTRAL" or "ACTION_REQUIRED" or "STALE" => CheckRunState.Neutral,
            _ => CheckRunState.Unknown,
        };
    }

    /// <summary>Maps a legacy commit status state onto <see cref="CheckRunState"/>.</summary>
    public static CheckRunState FromStatusContext(string? state) => state?.ToUpperInvariant() switch
    {
        "SUCCESS" => CheckRunState.Success,
        "FAILURE" => CheckRunState.Failure,
        "ERROR" => CheckRunState.Failure,
        "PENDING" => CheckRunState.Running,
        "EXPECTED" => CheckRunState.Queued,
        _ => CheckRunState.Unknown,
    };
}
