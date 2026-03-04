using System.Diagnostics;
using System.Text.Json;
using PrMonitor.Models;

namespace PrMonitor.Services;

/// <summary>
/// Talks to the GitHub GraphQL API through the <c>gh</c> CLI.
/// </summary>
public sealed class GitHubService
{
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
                commits(last: 1) {
                  nodes {
                    commit {
                      statusCheckRollup {
                        state
                      }
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
    /// Detect the authenticated GitHub username via <c>gh api user</c>.
    /// </summary>
    public async Task<string?> GetCurrentUserAsync()
    {
        var output = await RunGhAsync("api user -q .login");
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

    private static async Task<JsonElement?> RunGraphQlAsync(string query, string searchString)
    {
        // Escape the query for passing as -f argument
        var args = $"api graphql -f query=\"{EscapeForShell(query)}\" -f q=\"{EscapeForShell(searchString)}\"";
        var output = await RunGhAsync(args);
        if (string.IsNullOrWhiteSpace(output)) return null;

        try
        {
            using var doc = JsonDocument.Parse(output);
            // Clone so we can dispose the document
            return doc.RootElement.Clone();
        }
        catch (JsonException)
        {
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

            result.Add(new PullRequestInfo
            {
                Number = node.GetProperty("number").GetInt32(),
                Title = node.GetProperty("title").GetString() ?? "",
                Url = node.GetProperty("url").GetString() ?? "",
                Repository = node.GetProperty("repository").GetProperty("nameWithOwner").GetString() ?? "",
                Author = node.TryGetProperty("author", out var author)
                    ? author.GetProperty("login").GetString() ?? ""
                    : "",
                CreatedAt = node.TryGetProperty("createdAt", out var ca)
                    ? DateTimeOffset.Parse(ca.GetString()!)
                    : DateTimeOffset.MinValue,
                HasAutoMerge = hasAutoMerge,
                IsDraft = isDraft,
                CIState = ciState,
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

            result.Add(new PullRequestInfo
            {
                Number = node.GetProperty("number").GetInt32(),
                Title = node.GetProperty("title").GetString() ?? "",
                Url = node.GetProperty("url").GetString() ?? "",
                Repository = node.GetProperty("repository").GetProperty("nameWithOwner").GetString() ?? "",
                Author = node.TryGetProperty("author", out var author)
                    ? author.GetProperty("login").GetString() ?? ""
                    : "",
                CreatedAt = node.TryGetProperty("createdAt", out var ca)
                    ? DateTimeOffset.Parse(ca.GetString()!)
                    : DateTimeOffset.MinValue,
                BaseRefName = node.TryGetProperty("baseRefName", out var brn)
                    ? brn.GetString() ?? ""
                    : "",
                CIState = ciState,
            });
        }

        return result;
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

    private static async Task<string?> RunGhAsync(string arguments)
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
            await process.WaitForExitAsync();

            return process.ExitCode == 0 ? output : null;
        }
        catch
        {
            // gh CLI not installed or not on PATH
            return null;
        }
    }

    /// <summary>
    /// Minimal escaping for passing strings as gh CLI arguments.
    /// </summary>
    private static string EscapeForShell(string value) =>
        value.Replace("\\", "\\\\").Replace("\"", "\\\"");
}
