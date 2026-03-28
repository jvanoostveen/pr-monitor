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

    // Safe patterns for values interpolated into subprocess arguments.
    private static readonly System.Text.RegularExpressions.Regex _safeSlug =
        new(@"^[a-zA-Z0-9_.\-]+$", System.Text.RegularExpressions.RegexOptions.Compiled);
    private static readonly System.Text.RegularExpressions.Regex _safeSha =
        new(@"^[0-9a-fA-F]{1,40}$", System.Text.RegularExpressions.RegexOptions.Compiled);

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
                updatedAt
                isDraft
                mergeable
                headRefName
                reviewDecision
                autoMergeRequest { enabledAt }
                reviewRequests(first: 10) {
                  nodes {
                    requestedReviewer {
                      __typename
                      ... on User { login }
                      ... on Team { slug }
                    }
                  }
                }
                commits(last: 1) {
                  nodes {
                    commit {
                      oid
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
                updatedAt
                baseRefName
                mergeable
                headRefName
                reviewDecision
                reviewRequests(first: 10) {
                  nodes {
                    requestedReviewer {
                      __typename
                      ... on User { login }
                      ... on Team { slug }
                    }
                  }
                }
                commits(last: 1) {
                  nodes {
                    commit {
                      oid
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

    private const string ReviewRequestedFullQuery = """
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
                updatedAt
                baseRefName
                mergeable
                headRefName
                reviewDecision
                reviewRequests(first: 10) {
                  nodes {
                    requestedReviewer {
                      __typename
                      ... on User { login }
                      ... on Team { slug }
                    }
                  }
                }
                commits(last: 1) {
                  nodes {
                    commit {
                      oid
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
    public async Task<List<PullRequestInfo>> FetchPRsAwaitingMyReviewAsync(IReadOnlyList<string> organizations, bool classifyTeams = false, string? currentUsername = null)
    {
        var allPrs = new List<PullRequestInfo>();
        var queries = BuildSearchQueries("is:pr is:open review-requested:@me", organizations);
        var queryToUse = classifyTeams ? ReviewRequestedFullQuery : ReviewRequestedQuery;

        foreach (var q in queries)
        {
            var json = await RunGraphQlAsync(queryToUse, q);
            if (json is not { } jsonValue) continue;

            allPrs.AddRange(ParseReviewPrs(jsonValue, currentUsername));
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
        var (output, _, _) = await RunGhAsync("api", "user", "-q", ".login");
        return string.IsNullOrWhiteSpace(output) ? null : output.Trim();
    }

    /// <summary>
    /// Returns the IDs of failed GitHub Actions workflow runs for the given commit SHA.
    /// </summary>
    public async Task<IReadOnlyList<long>> FetchFailedRunIdsAsync(string owner, string repo, string headSha)
    {
        if (!ValidateSlug(owner, "owner") || !ValidateSlug(repo, "repo") || !ValidateSha(headSha))
            return [];
        var (output, stderr, exitCode) = await RunGhAsync(
            "api", $"repos/{owner}/{repo}/actions/runs?head_sha={headSha}",
            "--jq", ".workflow_runs[] | select(.conclusion==\"failure\") | .id");
        if (exitCode != 0 || string.IsNullOrWhiteSpace(output))
        {
            if (exitCode != 0)
                _logger.Warn($"FetchFailedRunIdsAsync failed (exit={exitCode}) for {owner}/{repo}@{headSha}: {stderr?.Trim()}");
            return [];
        }

        var ids = new List<long>();
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (long.TryParse(line, out var id))
                ids.Add(id);
        }
        return ids;
    }

    /// <summary>
    /// Fetches the failed job log output for a workflow run (truncated to 4000 chars).
    /// </summary>
    public async Task<string> FetchFailedLogAsync(string owner, string repo, long runId)
    {
        if (!ValidateSlug(owner, "owner") || !ValidateSlug(repo, "repo"))
            return "";
        var (output, _, exitCode) = await RunGhAsync("run", "view", runId.ToString(), "--log-failed", "--repo", $"{owner}/{repo}");
        if (exitCode != 0 || string.IsNullOrWhiteSpace(output))
            return "";

        const int MaxLength = 4000;
        return output.Length <= MaxLength ? output : output[..MaxLength] + "\n[truncated]";
    }

    /// <summary>
    /// Reruns failed jobs for a workflow run. Returns true on success.
    /// </summary>
    public async Task<bool> RerunFailedJobsAsync(string owner, string repo, long runId)
    {
        if (!ValidateSlug(owner, "owner") || !ValidateSlug(repo, "repo"))
            return false;
        var (_, stderr, exitCode) = await RunGhAsync("run", "rerun", runId.ToString(), "--failed", "--repo", $"{owner}/{repo}");
        if (exitCode != 0)
            _logger.Warn($"RerunFailedJobsAsync failed (exit={exitCode}) for run {runId} in {owner}/{repo}: {stderr?.Trim()}");
        return exitCode == 0;
    }

    /// <summary>
    /// Requests a Copilot review for the given pull request.
    /// Returns true on success.
    /// </summary>
    public async Task<bool> RequestCopilotReviewAsync(string owner, string repo, int prNumber)
    {
        if (!ValidateSlug(owner, "owner") || !ValidateSlug(repo, "repo"))
            return false;
        var (_, stderr, exitCode) = await RunGhAsync("pr", "edit", prNumber.ToString(), "--add-reviewer", "copilot", "--repo", $"{owner}/{repo}");
        if (exitCode != 0)
            _logger.Warn($"RequestCopilotReviewAsync failed (exit={exitCode}) for {owner}/{repo}#{prNumber}: {stderr?.Trim()}");
        return exitCode == 0;
    }

    // ── Internal helpers ────────────────────────────────────────────────

    internal static List<string> BuildSearchQueries(string baseQuery, IReadOnlyList<string> orgs)
    {
        if (orgs.Count == 0)
            return [baseQuery];

        // GitHub search supports org: qualifier – one query per org to stay
        // within the search-query length limits.
        return orgs.Select(org => $"{baseQuery} org:{org}").ToList();
    }

    private async Task<JsonElement?> RunGraphQlAsync(string query, string searchString)
    {
        var (output, stderr, exitCode) = await RunGhAsync(
            "api", "graphql", "-f", $"query={query}", "-f", $"q={searchString}");
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

    internal static List<PullRequestInfo> ParseMyPrs(JsonElement root)
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
                UpdatedAt = DateTimeOffset.TryParse(node.TryGetProperty("updatedAt", out var upd1) ? upd1.GetString() : null, out var updVal1) ? updVal1 : DateTimeOffset.MinValue,
                HasAutoMerge = hasAutoMerge,
                IsDraft = isDraft,
                HeadRefName = node.TryGetProperty("headRefName", out var hrn1)
                    ? hrn1.GetString() ?? ""
                    : "",
                HeadCommitSha = GetCommitOid(node),
                CIState = ciState,
                IsApproved = node.TryGetProperty("reviewDecision", out var rd1)
                    && rd1.GetString() == "APPROVED",
                UnresolvedReviewCommentCount = ParseUnresolvedReviewCommentCount(node),
                ReviewerLogins = ParseReviewerLogins(node),
            });
        }

        return result;
    }

    internal static List<PullRequestInfo> ParseReviewPrs(JsonElement root, string? currentUsername = null)
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

            // Classify as team-only when the current user has no direct User-type review request.
            // Other User-type reviewers (different people) do NOT make this a direct request for us.
            bool isTeamOnly = false;
            if (node.TryGetProperty("reviewRequests", out var reviewRequests)
                && reviewRequests.ValueKind == JsonValueKind.Object
                && reviewRequests.TryGetProperty("nodes", out var rrNodes)
                && rrNodes.ValueKind == JsonValueKind.Array)
            {
                var rrList = rrNodes.EnumerateArray().ToList();
                if (rrList.Count > 0)
                {
                    // Direct request = a User reviewer whose login matches the current user
                    bool directForMe = !string.IsNullOrEmpty(currentUsername) && rrList.Any(rr =>
                        rr.TryGetProperty("requestedReviewer", out var reviewer)
                        && reviewer.TryGetProperty("__typename", out var tn)
                        && tn.GetString() == "User"
                        && reviewer.TryGetProperty("login", out var login)
                        && string.Equals(login.GetString(), currentUsername, StringComparison.OrdinalIgnoreCase));
                    isTeamOnly = !directForMe;
                }
            }

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
                UpdatedAt = DateTimeOffset.TryParse(node.TryGetProperty("updatedAt", out var upd2) ? upd2.GetString() : null, out var updVal2) ? updVal2 : DateTimeOffset.MinValue,
                BaseRefName = node.TryGetProperty("baseRefName", out var brn)
                    ? brn.GetString() ?? ""
                    : "",
                HeadRefName = node.TryGetProperty("headRefName", out var hrn2)
                    ? hrn2.GetString() ?? ""
                    : "",
                HeadCommitSha = GetCommitOid(node),
                CIState = ciState,
                IsApproved = node.TryGetProperty("reviewDecision", out var rd2)
                    && rd2.GetString() == "APPROVED",
                UnresolvedReviewCommentCount = ParseUnresolvedReviewCommentCount(node),
                ReviewerLogins = ParseReviewerLogins(node),
                IsTeamReviewRequested = isTeamOnly,
            });
        }

        return result;
    }

    internal static IReadOnlyList<string> ParseReviewerLogins(JsonElement node)
    {
        if (!node.TryGetProperty("reviewRequests", out var reviewRequests)) return [];
        if (reviewRequests.ValueKind != JsonValueKind.Object) return [];
        if (!reviewRequests.TryGetProperty("nodes", out var nodes)) return [];
        if (nodes.ValueKind != JsonValueKind.Array) return [];

        var logins = new List<string>();
        foreach (var requestNode in nodes.EnumerateArray())
        {
            if (!requestNode.TryGetProperty("requestedReviewer", out var reviewer)) continue;
            if (!reviewer.TryGetProperty("__typename", out var typename)) continue;

            var type = typename.GetString();
            if (type == "User")
            {
                if (!reviewer.TryGetProperty("login", out var login)) continue;
                var loginStr = login.GetString() ?? "";
                if (string.IsNullOrEmpty(loginStr)) continue;
                if (string.Equals(loginStr, "copilot", StringComparison.OrdinalIgnoreCase)) continue;
                logins.Add(loginStr);
            }
            else if (type == "Team")
            {
                if (!reviewer.TryGetProperty("slug", out var slug)) continue;
                var slugStr = slug.GetString() ?? "";
                if (!string.IsNullOrEmpty(slugStr))
                    logins.Add(slugStr);
            }
        }
        return logins;
    }

    internal static int ParseUnresolvedReviewCommentCount(JsonElement node)
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

    internal static string GetAuthorLogin(JsonElement node)
    {
        if (!node.TryGetProperty("author", out var author))
            return "";
        if (author.ValueKind != JsonValueKind.Object)
            return "";
        if (!author.TryGetProperty("login", out var login))
            return "";

        return login.GetString() ?? "";
    }

    internal static string GetCommitOid(JsonElement node)
    {
        if (node.TryGetProperty("commits", out var commits)
            && commits.TryGetProperty("nodes", out var nodes))
        {
            foreach (var cn in nodes.EnumerateArray())
            {
                if (cn.TryGetProperty("commit", out var commit)
                    && commit.TryGetProperty("oid", out var oid))
                    return oid.GetString() ?? "";
            }
        }
        return "";
    }

    internal static CIState ParseCIState(string? state) => state?.ToUpperInvariant() switch
    {
        "SUCCESS" => CIState.Success,
        "FAILURE" => CIState.Failure,
        "PENDING" => CIState.Pending,
        "ERROR" => CIState.Error,
        "EXPECTED" => CIState.Success,
        _ => CIState.Unknown,
    };

    // ── Process helpers ─────────────────────────────────────────────────

    private async Task<(string? Output, string? Stderr, int ExitCode)> RunGhAsync(params string[] arguments)
    {
        try
        {
            using var process = new Process();
            process.StartInfo = new ProcessStartInfo
            {
                FileName = "gh",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            foreach (var arg in arguments)
                process.StartInfo.ArgumentList.Add(arg);

            process.Start();
            var outputTask = process.StandardOutput.ReadToEndAsync();
            var stderrTask = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            var output = await outputTask;
            var stderr = await stderrTask;

            if (process.ExitCode != 0)
            {
                _logger.Error($"GitHubService gh command failed (exit={process.ExitCode}). args: {string.Join(" ", arguments)}. stderr: {stderr?.Trim()}");
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

    private bool ValidateSlug(string value, string paramName)
    {
        if (_safeSlug.IsMatch(value)) return true;
        _logger.Warn($"GitHubService: rejected unsafe {paramName} value: '{value}'");
        return false;
    }

    private bool ValidateSha(string value)
    {
        if (_safeSha.IsMatch(value)) return true;
        _logger.Warn($"GitHubService: rejected unsafe headSha value: '{value}'");
        return false;
    }
}
