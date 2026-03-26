namespace PrMonitor.Models;

/// <summary>
/// Lightweight representation of a GitHub pull request with the fields
/// relevant for the PR Monitor.
/// </summary>
public sealed class PullRequestInfo
{
    /// <summary>PR number (e.g. 42).</summary>
    public int Number { get; init; }

    /// <summary>PR title.</summary>
    public required string Title { get; init; }

    /// <summary>Full URL to the PR on GitHub.</summary>
    public required string Url { get; init; }

    /// <summary>Repository in "owner/repo" format.</summary>
    public required string Repository { get; init; }

    /// <summary>Login of the PR author.</summary>
    public required string Author { get; init; }

    /// <summary>When the PR was created.</summary>
    public DateTimeOffset CreatedAt { get; init; }

    /// <summary>Whether auto-merge is enabled on this PR.</summary>
    public bool HasAutoMerge { get; init; }

    /// <summary>Whether this PR is a draft.</summary>
    public bool IsDraft { get; init; }

    /// <summary>Aggregate CI status of the latest commit.</summary>
    public CIState CIState { get; init; } = CIState.Unknown;

    /// <summary>Number of unresolved review comments across unresolved review threads.</summary>
    public int UnresolvedReviewCommentCount { get; init; }

    /// <summary>Whether this PR has been approved by at least one reviewer (reviewDecision == APPROVED).</summary>
    public bool IsApproved { get; init; }

    /// <summary>Base branch name (e.g. "release/1.2").</summary>
    public string BaseRefName { get; init; } = "";

    /// <summary>Head branch name (e.g. "feature/my-feature").</summary>
    public string HeadRefName { get; init; } = "";

    /// <summary>SHA of the latest commit on the head branch.</summary>
    public string HeadCommitSha { get; set; } = "";

    /// <summary>True when the review was requested for a team only, not directly from this user.</summary>
    public bool IsTeamReviewRequested { get; init; }

    /// <summary>
    /// Logins/slugs of pending reviewer requests, excluding Copilot.
    /// Users are identified by login; teams by slug.
    /// </summary>
    public IReadOnlyList<string> ReviewerLogins { get; init; } = [];

    /// <summary>
    /// Unique key used for delta-detection across polls.
    /// </summary>
    public string Key => $"{Repository}#{Number}";

    public override string ToString() =>
        $"{Repository}#{Number}: {Title} [{CIState}]";
}
