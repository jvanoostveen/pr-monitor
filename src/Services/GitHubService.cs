using System.Diagnostics;
using System.Text.Json;
using PrMonitor.Models;

namespace PrMonitor.Services;

/// <summary>
/// Talks to the GitHub GraphQL API through the <c>gh</c> CLI.
/// </summary>
public sealed class GitHubService
{
    private readonly DiagnosticsLogger _logger;

    public GitHubService(DiagnosticsLogger logger)
    {
        _logger = logger;
    }

    // ── GraphQL fragments ───────────────────────────────────────────────

    private const string MyPrsQuery = """
        query($q: String!) {
          search(query: $q, type: ISSUE, first: 50) {
            nodes {
              ... on PullRequest {
                number
                title
                url
                repository { nameWithOwner isArchived }
                author { login }
                createdAt
                isDraft
                mergeable
                headRefName
                reviewDecision
                autoMergeRequest { enabledAt }
                commits(last: 1) {
                  nodes {
                    commit {
                      statusCheckRollup {
                        state
                      }
                    }
                  }
                }
                                reviewThreads(first: 50) {
                                    nodes {
                                        isResolved
                                        comments(first: 1) {
                                            totalCount
                                        }
                                    }
                                }
              }
            }
          }
        }
        """;

    private const string ReviewRequestedQuery = """
        query($q: String!) {
          search(query: $q, type: ISSUE, first: 50) {
            nodes {
              ... on PullRequest {
                number
                title
                url
                repository { nameWithOwner isArchived }
                author { login }
                createdAt
                baseRefName
                mergeable
                headRefName
                reviewDecision
                commits(last: 1) {
                  nodes {
                    commit {
                      statusCheckRollup {
                        state
                      }
                    }
                  }
                }
                                reviewThreads(first: 50) {
                                    nodes {
                                        isResolved
                                        comments(first: 1) {
                                            totalCount
                                        }
                                    }
                                }
              }
            }
          }
        }
        """;

    // ── Public API ──────────────────────────────────────────────────────

    /// <summary>
    /// Fetch ALL open PRs authored by the current user (auto-merge and non-auto-merge).
    /// </summary>
    public async Task<List<PullRequestInfo>> FetchAllMyPRsAsync(IReadOnlyList<string> organizations)
    {
        var allPrs = new List<PullRequestInfo>();
        var queries = BuildSearchQueries("is:pr is:open author:@me", organizations);

        foreach (var q in queries)
        {
            var json = await RunGraphQlAsync(MyPrsQuery, q);
            if (json is not { } jsonValue) continue;
            allPrs.AddRange(ParseMyPrs(jsonValue));
        }

        return allPrs.DistinctBy(p => p.Key).ToList();
    }

    /// <summary>
    /// Fetch the current user's open PRs (optionally filtered by orgs),
    /// keeping only those that have auto-merge enabled.
    /// </summary>
    public async Task<List<PullRequestInfo>> FetchMyAutoMergePRsAsync(IReadOnlyList<string> organizations)
    {
        var all = await FetchAllMyPRsAsync(organizations);
        return all.Where(p => p.HasAutoMerge).ToList();
    }

    /// <summary>
    /// Fetch open PRs that target a <c>release/*</c> branch (hotfixes).
    /// Uses <c>involves:@me</c> to catch PRs authored by bots where the user
    /// is assignee, reviewer, mentioned, or has commented.
    /// </summary>
    public async Task<List<PullRequestInfo>> FetchHotfixPRsAsync(IReadOnlyList<string> organizations)
    {
        var allPrs = new List<PullRequestInfo>();

        foreach (var q in BuildSearchQueries("is:pr is:open involves:@me", organizations))
        {
            var json = await RunGraphQlAsync(ReviewRequestedQuery, q);
            if (json is not { } jsonValue) continue;
            allPrs.AddRange(
                ParseReviewPrs(jsonValue)
                    .Where(p => p.BaseRefName.StartsWith("release/", StringComparison.OrdinalIgnoreCase)));
        }

        return allPrs.DistinctBy(p => p.Key).ToList();
    }

    /// <summary>
    /// Fetch open PRs where the current user is a requested reviewer
    /// (i.e. hasn't reviewed yet), optionally filtered by orgs.
    /// </summary>
    public async Task<List<PullRequestInfo>> FetchPRsAwaitingMyReviewAsync(IReadOnlyList<string> organizations)
    {
        var allPrs = new List<PullRequestInfo>();
        var queries = BuildSearchQueries("is:pr is:open review-requested:@me", organizations);

        foreach (var q in queries)
        {
            var json = await RunGraphQlAsync(ReviewRequestedQuery, q);
            if (json is not { } jsonValue) continue;

            allPrs.AddRange(ParseReviewPrs(jsonValue));
        }

        return allPrs;
    }

    /// <summary>
    /// Fetch open PRs that have the current user as assignee.
    /// Used to surface Copilot-created PRs assigned to the user.
    /// </summary>
    public async Task<List<PullRequestInfo>> FetchMyAssignedPRsAsync(IReadOnlyList<string> organizations)
    {
        var allPrs = new List<PullRequestInfo>();
        var queries = BuildSearchQueries("is:pr is:open assignee:@me", organizations);

        foreach (var q in queries)
        {
            var json = await RunGraphQlAsync(ReviewRequestedQuery, q);
            if (json is not { } jsonValue) continue;
            allPrs.AddRange(ParseReviewPrs(jsonValue));
        }

        return allPrs.DistinctBy(p => p.Key).ToList();
    }

    /// <summary>
    /// Detect the authenticated GitHub username via <c>gh api user</c>.
    /// </summary>
    public async Task<string?> GetCurrentUserAsync()
    {
        var (output, _, _) = await RunGhAsync("api user -q .login");
        return string.IsNullOrWhiteSpace(output) ? null : output.Trim();
    }

    // ── Internal helpers ────────────────────────────────────────────────

    private static List<string> BuildSearchQueries(string baseQuery, IReadOnlyList<string> orgs)
    {
        if (orgs.Count == 0)
            return [baseQuery];

        // GitHub search supports org: qualifier – one query per org to stay
        // within the search-query length limits.
        return orgs.Select(org => $"{baseQuery} org:{org}").ToList();
    }

    private async Task<JsonElement?> RunGraphQlAsync(string query, string searchString)
    {
        // Escape the query for passing as -f argument
        var args = $"api graphql -f query=\"{EscapeForShell(query)}\" -f q=\"{EscapeForShell(searchString)}\"";
        var (output, stderr, exitCode) = await RunGhAsync(args);
        if (exitCode != 0)
        {
            _logger.Error($"GitHubService GraphQL call failed (exit={exitCode}) for query '{searchString}'. stderr: {stderr?.Trim()}");
            throw new InvalidOperationException($"gh api graphql failed (exit={exitCode}): {stderr?.Trim()}");
        }

        if (string.IsNullOrWhiteSpace(output))
        {
            _logger.Warn($"GitHubService GraphQL call returned empty output for query '{searchString}'.");
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(output);
            if (doc.RootElement.TryGetProperty("errors", out var errors) && errors.ValueKind == JsonValueKind.Array)
            {
                _logger.Warn($"GitHubService GraphQL response contains errors for query '{searchString}': {errors}");
            }
            // Clone so we can dispose the document
            return doc.RootElement.Clone();
        }
        catch (JsonException ex)
        {
            _logger.Error($"GitHubService JSON parse failed for GraphQL query '{searchString}'.", ex);
            return null;
        }
    }

    private static List<PullRequestInfo> ParseMyPrs(JsonElement root)
    {
        var result = new List<PullRequestInfo>();

        if (!root.TryGetProperty("data", out var data)) return result;
        if (!data.TryGetProperty("search", out var search)) return result;
        if (!search.TryGetProperty("nodes", out var nodes)) return result;

        foreach (var node in nodes.EnumerateArray())
        {
            // Skip nodes that didn't resolve as a PR (empty objects)
            if (!node.TryGetProperty("number", out _)) continue;

            // Skip PRs from archived repositories
            if (node.TryGetProperty("repository", out var repoNode)
                && repoNode.TryGetProperty("isArchived", out var isArchivedProp)
                && isArchivedProp.GetBoolean())
                continue;

            var hasAutoMerge = node.TryGetProperty("autoMergeRequest", out var amr)
                               && amr.ValueKind != JsonValueKind.Null;

            var isDraft = node.TryGetProperty("isDraft", out var draftProp)
                          && draftProp.ValueKind == JsonValueKind.True;

            var ciState = CIState.Unknown;
            if (node.TryGetProperty("commits", out var commits)
                && commits.TryGetProperty("nodes", out var commitNodes))
            {
                foreach (var cn in commitNodes.EnumerateArray())
                {
                    if (cn.TryGetProperty("commit", out var commit)
                        && commit.TryGetProperty("statusCheckRollup", out var rollup)
                        && rollup.ValueKind != JsonValueKind.Null
                        && rollup.TryGetProperty("state", out var state))
                    {
                        ciState = ParseCIState(state.GetString());
                    }
                }
            }

            if (node.TryGetProperty("mergeable", out var mergeableMyPr)
                && mergeableMyPr.GetString() == "CONFLICTING")
                ciState = CIState.Failure;

            result.Add(new PullRequestInfo
            {
                Number = node.GetProperty("number").GetInt32(),
                Title = node.GetProperty("title").GetString() ?? "",
                Url = node.GetProperty("url").GetString() ?? "",
                Repository = node.GetProperty("repository").GetProperty("nameWithOwner").GetString() ?? "",
                Author = GetAuthorLogin(node),
                CreatedAt = node.TryGetProperty("createdAt", out var ca)
                    ? DateTimeOffset.Parse(ca.GetString()!)
                    : DateTimeOffset.MinValue,
                HasAutoMerge = hasAutoMerge,
                IsDraft = isDraft,
                HeadRefName = node.TryGetProperty("headRefName", out var hrn1)
                    ? hrn1.GetString() ?? ""
                    : "",
                CIState = ciState,
                IsApproved = node.TryGetProperty("reviewDecision", out var rd1)
                    && rd1.GetString() == "APPROVED",
                UnresolvedReviewCommentCount = ParseUnresolvedReviewCommentCount(node),
            });
        }

        return result;
    }

    private static List<PullRequestInfo> ParseReviewPrs(JsonElement root)
    {
        var result = new List<PullRequestInfo>();

        if (!root.TryGetProperty("data", out var data)) return result;
        if (!data.TryGetProperty("search", out var search)) return result;
        if (!search.TryGetProperty("nodes", out var nodes)) return result;

        foreach (var node in nodes.EnumerateArray())
        {
            if (!node.TryGetProperty("number", out _)) continue;

            // Skip PRs from archived repositories
            if (node.TryGetProperty("repository", out var repoNode)
                && repoNode.TryGetProperty("isArchived", out var isArchivedProp)
                && isArchivedProp.GetBoolean())
                continue;

            var ciState = CIState.Unknown;
            if (node.TryGetProperty("commits", out var commits)
                && commits.TryGetProperty("nodes", out var commitNodes))
            {
                foreach (var cn in commitNodes.EnumerateArray())
                {
                    if (cn.TryGetProperty("commit", out var commit)
                        && commit.TryGetProperty("statusCheckRollup", out var rollup)
                        && rollup.ValueKind != JsonValueKind.Null
                        && rollup.TryGetProperty("state", out var state))
                    {
                        ciState = ParseCIState(state.GetString());
                    }
                }
            }

            if (node.TryGetProperty("mergeable", out var mergeableReview)
                && mergeableReview.GetString() == "CONFLICTING")
                ciState = CIState.Failure;

            result.Add(new PullRequestInfo
            {
                Number = node.GetProperty("number").GetInt32(),
                Title = node.GetProperty("title").GetString() ?? "",
                Url = node.GetProperty("url").GetString() ?? "",
                Repository = node.GetProperty("repository").GetProperty("nameWithOwner").GetString() ?? "",
                Author = GetAuthorLogin(node),
                CreatedAt = node.TryGetProperty("createdAt", out var ca)
                    ? DateTimeOffset.Parse(ca.GetString()!)
                    : DateTimeOffset.MinValue,
                BaseRefName = node.TryGetProperty("baseRefName", out var brn)
                    ? brn.GetString() ?? ""
                    : "",
                HeadRefName = node.TryGetProperty("headRefName", out var hrn2)
                    ? hrn2.GetString() ?? ""
                    : "",
                CIState = ciState,
                IsApproved = node.TryGetProperty("reviewDecision", out var rd2)
                    && rd2.GetString() == "APPROVED",
                UnresolvedReviewCommentCount = ParseUnresolvedReviewCommentCount(node),
            });
        }

        return result;
    }

    private static int ParseUnresolvedReviewCommentCount(JsonElement node)
    {
        if (!node.TryGetProperty("reviewThreads", out var reviewThreads)) return 0;
        if (reviewThreads.ValueKind != JsonValueKind.Object) return 0;
        if (!reviewThreads.TryGetProperty("nodes", out var threadNodes)) return 0;
        if (threadNodes.ValueKind != JsonValueKind.Array) return 0;

        var unresolvedComments = 0;
        foreach (var thread in threadNodes.EnumerateArray())
        {
            if (thread.ValueKind != JsonValueKind.Object)
                continue;

            var isResolved = thread.TryGetProperty("isResolved", out var resolvedNode)
                             && resolvedNode.ValueKind == JsonValueKind.True;
            if (isResolved) continue;

            if (thread.TryGetProperty("comments", out var comments)
                && comments.ValueKind == JsonValueKind.Object
                && comments.TryGetProperty("totalCount", out var totalCount)
                && totalCount.TryGetInt32(out var count))
            {
                unresolvedComments += count;
            }
            else
            {
                unresolvedComments += 1;
            }
        }

        return unresolvedComments;
    }

    private static string GetAuthorLogin(JsonElement node)
    {
        if (!node.TryGetProperty("author", out var author))
            return "";
        if (author.ValueKind != JsonValueKind.Object)
            return "";
        if (!author.TryGetProperty("login", out var login))
            return "";

        return login.GetString() ?? "";
    }

    private static CIState ParseCIState(string? state) => state?.ToUpperInvariant() switch
    {
        "SUCCESS" => CIState.Success,
        "FAILURE" => CIState.Failure,
        "PENDING" => CIState.Pending,
        "ERROR" => CIState.Error,
        "EXPECTED" => CIState.Success,
        _ => CIState.Unknown,
    };

    // ── Process helpers ─────────────────────────────────────────────────

    private async Task<(string? Output, string? Stderr, int ExitCode)> RunGhAsync(string arguments)
    {
        try
        {
            using var process = new Process();
            process.StartInfo = new ProcessStartInfo
            {
                FileName = "gh",
                Arguments = arguments,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            process.Start();
            var output = await process.StandardOutput.ReadToEndAsync();
            var stderr = await process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();

            if (process.ExitCode != 0)
            {
                _logger.Error($"GitHubService gh command failed (exit={process.ExitCode}). args: {arguments}. stderr: {stderr?.Trim()}");
            }

            return (process.ExitCode == 0 ? output : null, stderr, process.ExitCode);
        }
        catch (Exception ex)
        {
            // gh CLI not installed or not on PATH
            _logger.Error("GitHubService failed to start gh process.", ex);
            return (null, null, -1);
        }
    }

    /// <summary>
    /// Minimal escaping for passing strings as gh CLI arguments.
    /// </summary>
    private static string EscapeForShell(string value) =>
        value.Replace("\\", "\\\\").Replace("\"", "\\\"");
}
