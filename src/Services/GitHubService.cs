using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
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
        query($q: String!, $cursor: String) {
          search(query: $q, type: ISSUE, first: 50, after: $cursor) {
            pageInfo { hasNextPage endCursor }
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
        query($q: String!, $cursor: String) {
          search(query: $q, type: ISSUE, first: 50, after: $cursor) {
            pageInfo { hasNextPage endCursor }
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
        query($q: String!, $cursor: String) {
          search(query: $q, type: ISSUE, first: 50, after: $cursor) {
            pageInfo { hasNextPage endCursor }
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

        const int MaxPages = 5;
        foreach (var q in queries)
        {
            string? cursor = null;
            for (int page = 0; page < MaxPages; page++)
            {
                var json = await RunGraphQlAsync(MyPrsQuery, q, cursor);
                if (json is not { } jsonValue) break;
                allPrs.AddRange(ParseMyPrs(jsonValue));
                if (!jsonValue.TryGetProperty("data", out var d) ||
                    !d.TryGetProperty("search", out var s) ||
                    !s.TryGetProperty("pageInfo", out var pi) ||
                    !pi.GetProperty("hasNextPage").GetBoolean()) break;
                cursor = pi.GetProperty("endCursor").GetString();
                if (cursor is null) break;
            }
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

        const int MaxPages = 5;
        foreach (var q in BuildSearchQueries("is:pr is:open involves:@me", organizations))
        {
            string? cursor = null;
            for (int page = 0; page < MaxPages; page++)
            {
                var json = await RunGraphQlAsync(ReviewRequestedQuery, q, cursor);
                if (json is not { } jsonValue) break;
                allPrs.AddRange(
                    ParseReviewPrs(jsonValue)
                        .Where(p => p.BaseRefName.StartsWith("release/", StringComparison.OrdinalIgnoreCase)));
                if (!jsonValue.TryGetProperty("data", out var d) ||
                    !d.TryGetProperty("search", out var s) ||
                    !s.TryGetProperty("pageInfo", out var pi) ||
                    !pi.GetProperty("hasNextPage").GetBoolean()) break;
                cursor = pi.GetProperty("endCursor").GetString();
                if (cursor is null) break;
            }
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

        const int MaxPages = 5;
        foreach (var q in queries)
        {
            string? cursor = null;
            for (int page = 0; page < MaxPages; page++)
            {
                var json = await RunGraphQlAsync(queryToUse, q, cursor);
                if (json is not { } jsonValue) break;
                allPrs.AddRange(ParseReviewPrs(jsonValue, currentUsername));
                if (!jsonValue.TryGetProperty("data", out var d) ||
                    !d.TryGetProperty("search", out var s) ||
                    !s.TryGetProperty("pageInfo", out var pi) ||
                    !pi.GetProperty("hasNextPage").GetBoolean()) break;
                cursor = pi.GetProperty("endCursor").GetString();
                if (cursor is null) break;
            }
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
    /// Fetch unread @mention notifications for pull requests via the GitHub Notifications API.
    /// Returns a list of (Id, Title, Repo) tuples. Returns empty list on any failure.
    /// </summary>
    public async Task<IReadOnlyList<(string Id, string Title, string Repo, DateTimeOffset UpdatedAt, string PrUrl)>> FetchMentionNotificationsAsync()
    {
        try
        {
            var (output, stderr, exitCode) = await RunGhAsync("api", "/notifications?per_page=50");
            if (exitCode != 0 || string.IsNullOrWhiteSpace(output))
            {
                if (exitCode != 0)
                    _logger.Warn($"FetchMentionNotificationsAsync failed (exit={exitCode}): {stderr?.Trim()}");
                return [];
            }

            using var doc = JsonDocument.Parse(output);
            var result = new List<(string, string, string, DateTimeOffset, string)>();
            foreach (var element in doc.RootElement.EnumerateArray())
            {
                if (!element.TryGetProperty("unread", out var unreadProp) || !unreadProp.GetBoolean())
                    continue;
                // Only direct @username mentions, not team mentions (@org/team)
                if (!element.TryGetProperty("reason", out var reasonProp) || reasonProp.GetString() != "mention")
                    continue;
                if (!element.TryGetProperty("subject", out var subject))
                    continue;
                if (subject.TryGetProperty("type", out var typeProp) && typeProp.GetString() != "PullRequest")
                    continue;
                if (!element.TryGetProperty("id", out var idProp))
                    continue;
                var id = idProp.GetString();
                var title = subject.TryGetProperty("title", out var titleProp) ? titleProp.GetString() : null;
                var repo = element.TryGetProperty("repository", out var repoProp)
                    && repoProp.TryGetProperty("full_name", out var fullNameProp)
                    ? fullNameProp.GetString()
                    : null;
                var updatedAt = element.TryGetProperty("updated_at", out var updatedAtProp)
                    && DateTimeOffset.TryParse(updatedAtProp.GetString(), out var parsed)
                    ? parsed
                    : DateTimeOffset.MinValue;
                // Convert API URL (api.github.com/repos/owner/repo/pulls/123)
                // to web URL (github.com/owner/repo/pull/123)
                var prUrl = "";
                if (subject.TryGetProperty("url", out var urlProp))
                {
                    var apiUrl = urlProp.GetString() ?? "";
                    prUrl = System.Text.RegularExpressions.Regex.Replace(
                        apiUrl,
                        @"^https://api\.github\.com/repos/(.+)/pulls/(\d+)$",
                        "https://github.com/$1/pull/$2");
                    if (!prUrl.StartsWith("https://github.com/")) prUrl = "";
                }
                if (id is not null && title is not null && repo is not null)
                    result.Add((id, title, repo, updatedAt, prUrl));
            }
            return result;
        }
        catch (Exception ex)
        {
            _logger.Error("FetchMentionNotificationsAsync failed.", ex);
            return [];
        }
    }

    /// <summary>
    /// Marks a GitHub notification thread as read so it won't be returned in future API calls.
    /// Fire-and-forget safe — failures are logged and silently swallowed.
    /// </summary>
    public async Task MarkNotificationReadAsync(string notificationId)
    {
        try
        {
            var (_, _, exitCode) = await RunGhAsync("api", "-X", "PATCH", $"/notifications/threads/{notificationId}");
            if (exitCode != 0)
                _logger.Warn($"MarkNotificationReadAsync: non-zero exit for id={notificationId}");
        }
        catch (Exception ex)
        {
            _logger.Error("MarkNotificationReadAsync failed.", ex);
        }
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

        var sanitized = SanitizeLogForAI(output);

        const int MaxLength = 4000;
        return sanitized.Length <= MaxLength ? sanitized : sanitized[..MaxLength] + "\n[truncated]";
    }

    /// <summary>
    /// Removes lines from a CI log that are likely to trigger Azure OpenAI's content
    /// filter (jailbreak detection). These lines contain prompt-injection-like phrases
    /// or instructions that are meaningless for flakiness analysis anyway.
    /// </summary>
    private static string SanitizeLogForAI(string log)
    {
        // Patterns that trigger Azure OpenAI jailbreak detection.
        // Matched case-insensitively against individual lines.
        var injectionPatterns = new[]
        {
            @"ignore\s+(all\s+)?(previous|prior|above|earlier)\s+(instructions?|prompts?|context|rules?)",
            @"(you\s+are\s+now|act\s+as|pretend\s+(to\s+be|you\s+are)|roleplay)\b",
            @"(system\s+prompt|initial\s+prompt|forget\s+your\s+(instructions?|training))",
            @"(jailbreak|DAN\b|do\s+anything\s+now)",
            @"(disregard|override|bypass|circumvent)\s+(all\s+)?(your\s+)?(safety|restrictions?|guidelines?|policies?|rules?)",
        };

        var compiled = injectionPatterns
            .Select(p => new Regex(p, RegexOptions.IgnoreCase | RegexOptions.Compiled))
            .ToArray();

        var lines = log.Split('\n');
        var result = new System.Text.StringBuilder(log.Length);
        int redacted = 0;

        foreach (var line in lines)
        {
            if (compiled.Any(r => r.IsMatch(line)))
            {
                result.AppendLine("[line redacted]");
                redacted++;
            }
            else
            {
                result.AppendLine(line);
            }
        }

        return result.ToString();
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
    /// Uses the REST API directly because the Copilot reviewer is a GitHub App bot,
    /// which cannot be resolved via GraphQL requestReviewsByLogin (used by gh pr edit).
    /// Returns true on success.
    /// </summary>
    public async Task<bool> RequestCopilotReviewAsync(string owner, string repo, int prNumber)
    {
        if (!ValidateSlug(owner, "owner") || !ValidateSlug(repo, "repo"))
            return false;
        var (_, stderr, exitCode) = await RunGhAsync(
            "api",
            $"repos/{owner}/{repo}/pulls/{prNumber}/requested_reviewers",
            "--method", "POST",
            "-f", "reviewers[]=copilot-pull-request-reviewer[bot]");
        if (exitCode != 0)
            _logger.Warn($"RequestCopilotReviewAsync failed (exit={exitCode}) for {owner}/{repo}#{prNumber}: {stderr?.Trim()}");
        return exitCode == 0;
    }

    /// <summary>
    /// Converts a draft PR to ready for review. Returns true on success.
    /// </summary>
    public async Task<bool> SetPrReadyAsync(string owner, string repo, int prNumber)
    {
        if (!ValidateSlug(owner, "owner") || !ValidateSlug(repo, "repo"))
            return false;
        var (_, stderr, exitCode) = await RunGhAsync("pr", "ready", prNumber.ToString(), "--repo", $"{owner}/{repo}");
        if (exitCode != 0)
            _logger.Warn($"SetPrReadyAsync failed (exit={exitCode}) for {owner}/{repo}#{prNumber}: {stderr?.Trim()}");
        return exitCode == 0;
    }

    /// <summary>
    /// Converts a ready PR to draft. Returns true on success.
    /// </summary>
    public async Task<bool> SetPrDraftAsync(string owner, string repo, int prNumber)
    {
        if (!ValidateSlug(owner, "owner") || !ValidateSlug(repo, "repo"))
            return false;
        var (_, stderr, exitCode) = await RunGhAsync("pr", "edit", prNumber.ToString(), "--draft", "--repo", $"{owner}/{repo}");
        if (exitCode != 0)
            _logger.Warn($"SetPrDraftAsync failed (exit={exitCode}) for {owner}/{repo}#{prNumber}: {stderr?.Trim()}");
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

    private async Task<JsonElement?> RunGraphQlAsync(string query, string searchString, string? cursor = null)
    {
        var ghArgs = cursor is null
            ? new[] { "api", "graphql", "-f", $"query={query}", "-f", $"q={searchString}" }
            : new[] { "api", "graphql", "-f", $"query={query}", "-f", $"q={searchString}", "-f", $"cursor={cursor}" };
        var (output, stderr, exitCode) = await RunGhAsync(ghArgs);
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
                if (loginStr.StartsWith("copilot", StringComparison.OrdinalIgnoreCase)) continue;
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
