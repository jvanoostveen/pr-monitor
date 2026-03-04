namespace PrBot.Models;

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

    /// <summary>Base branch name (e.g. "release/1.2").</summary>
    public string BaseRefName { get; init; } = "";

    /// <summary>
    /// Unique key used for delta-detection across polls.
    /// </summary>
    public string Key => $"{Repository}#{Number}";

    public override string ToString() =>
        $"{Repository}#{Number}: {Title} [{CIState}]";
}
