namespace PrMonitor.Models;

/// <summary>
/// Outcome of a check-run fetch. The distinction matters for auto-refresh: a rate-limited
/// call must never be retried on a timer, while a plain failure is simply shown to the user.
/// </summary>
public enum CheckFetchStatus
{
    /// <summary>GitHub answered; <see cref="CheckFetchResult.Checks"/> holds the result.</summary>
    Ok,

    /// <summary>The call failed for a reason unrelated to rate limiting.</summary>
    Failed,

    /// <summary>GitHub refused the call because a (primary or secondary) rate limit was hit.</summary>
    RateLimited,
}

/// <summary>
/// Result of <see cref="Services.GitHubService.FetchPrChecksAsync"/>: the checks plus what the
/// GraphQL API reported about the remaining rate-limit budget.
/// </summary>
public sealed class CheckFetchResult
{
    /// <summary>Checks on the PR head commit; empty unless <see cref="Status"/> is <see cref="CheckFetchStatus.Ok"/>.</summary>
    public IReadOnlyList<CheckRunInfo> Checks { get; init; } = [];

    public CheckFetchStatus Status { get; init; } = CheckFetchStatus.Ok;

    /// <summary>Points left in the current GraphQL rate-limit window, when GitHub reported it.</summary>
    public int? RateLimitRemaining { get; init; }

    /// <summary>When the current rate-limit window resets, when GitHub reported it.</summary>
    public DateTimeOffset? RateLimitResetAt { get; init; }

    /// <summary>True when the call did not produce usable data.</summary>
    public bool IsFailure => Status != CheckFetchStatus.Ok;

    public static CheckFetchResult Success(
        IReadOnlyList<CheckRunInfo> checks,
        int? remaining = null,
        DateTimeOffset? resetAt = null) =>
        new() { Checks = checks, Status = CheckFetchStatus.Ok, RateLimitRemaining = remaining, RateLimitResetAt = resetAt };

    public static CheckFetchResult Failure() =>
        new() { Status = CheckFetchStatus.Failed };

    public static CheckFetchResult RateLimited(DateTimeOffset? resetAt = null) =>
        new() { Status = CheckFetchStatus.RateLimited, RateLimitRemaining = 0, RateLimitResetAt = resetAt };
}
