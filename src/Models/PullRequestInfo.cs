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

    /// <summary>When the PR was last updated.</summary>
    public DateTimeOffset UpdatedAt { get; init; }

    /// <summary>Whether auto-merge is enabled on this PR.</summary>
    public bool HasAutoMerge { get; init; }

    /// <summary>Whether this PR is a draft.</summary>
    public bool IsDraft { get; init; }

    /// <summary>Aggregate CI status of the latest commit.</summary>
    public CIState CIState { get; init; } = CIState.Unknown;

    /// <summary>Whether the PR has merge conflicts (mergeable == CONFLICTING).</summary>
    public bool HasConflicts { get; set; }

    /// <summary>Whether GitHub returned "UNKNOWN" for mergeability (lazily computed, not yet resolved).</summary>
    public bool IsMergeabilityUnknown { get; init; }

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

    // ── Stacked-PR relations (derived locally, see PollingService.ApplyStackRelations) ──

    /// <summary>Key of the PR this one is stacked on: its base branch is that PR's head branch.</summary>
    public string? StackParentKey { get; set; }

    /// <summary>Number of the stack parent PR, or 0 when there is none.</summary>
    public int StackParentNumber { get; set; }

    /// <summary>URL of the stack parent PR, or empty when there is none.</summary>
    public string StackParentUrl { get; set; } = "";

    /// <summary>Author of the stack parent PR, or empty when there is none.</summary>
    public string StackParentAuthor { get; set; } = "";

    /// <summary>Key of the bottom-most PR of the stack; equals <see cref="Key"/> for the root itself.</summary>
    public string? StackRootKey { get; set; }

    /// <summary>0 for the bottom PR of a stack, incremented per level upwards.</summary>
    public int StackDepth { get; set; }

    /// <summary>Total number of open PRs in this stack; 1 when the PR is not stacked.</summary>
    public int StackSize { get; set; } = 1;

    /// <summary>True when this PR belongs to a stack of two or more open PRs.</summary>
    public bool IsStacked => StackSize > 1;

    /// <summary>True when an open parent PR has to be merged before this one can be merged.</summary>
    public bool IsBlockedByStack => !string.IsNullOrEmpty(StackParentKey);

    /// <summary>
    /// Logins/slugs of pending reviewer requests, excluding Copilot.
    /// Users are identified by login; teams by slug.
    /// </summary>
    public IReadOnlyList<string> ReviewerLogins { get; init; } = [];

    /// <summary>
    /// Latest review state (Pending/Approved/ChangesRequested/Commented) per reviewer login, excluding Copilot.
    /// A login present in <see cref="ReviewerLogins"/> but absent here should be treated as Pending.
    /// </summary>
    public IReadOnlyDictionary<string, ReviewState> ReviewerStates { get; init; } = new Dictionary<string, ReviewState>();

    /// <summary>
    /// Unique key used for delta-detection across polls.
    /// </summary>
    public string Key => $"{Repository}#{Number}";

    public override string ToString() =>
        $"{Repository}#{Number}: {Title} [{CIState}{(HasConflicts ? " CONFLICTING" : "")}{(IsMergeabilityUnknown ? " UNKNOWN_MERGEABLE" : "")}]";
}
