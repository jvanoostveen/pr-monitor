namespace PrMonitor.Models;

/// <summary>
/// Context about a CI failure on a pull request. Passed to flakiness analysis.
/// </summary>
public sealed class FailureContext
{
    public required string Repository { get; init; }
    public required int PrNumber { get; init; }
    public required string PrTitle { get; init; }
    public required string HeadBranch { get; init; }
    public required string HeadCommitSha { get; init; }
    public required IReadOnlyList<string> FailedCheckNames { get; init; }
    /// <summary>Log output from 'gh run view --log-failed', truncated to 4000 chars.</summary>
    public required string LogExcerpt { get; init; }
}
