using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using PrMonitor.Models;

namespace PrMonitor.Services;

/// <summary>
/// Thrown when a GitHub API call could not be completed. Callers must never interpret
/// this as "there is no data".
/// </summary>
public sealed class GitHubApiException : Exception
{
    public GitHubApiException(string message) : base(message) { }
    public GitHubApiException(string message, Exception inner) : base(message, inner) { }
}

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

    // ── Log-sanitization patterns ───────────────────────────────────────
    // All compiled once: RegexOptions.Compiled emits dynamic IL, so constructing these
    // per call would grow the process's code heaps on every analysed CI failure.

    /// <summary>GitHub Actions prepends an ISO-8601 timestamp to every log line.</summary>
    private static readonly Regex _timestampPrefix =
        new(@"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d+Z\s*", RegexOptions.Compiled);

    /// <summary>ANSI escape sequences (colors, cursor movement) used by some test runners.</summary>
    private static readonly Regex _ansiEscape =
        new(@"\x1B(?:\[[0-9;]*[mGKHFJABCDH]|[()][0-9A-Za-z])", RegexOptions.Compiled);

    /// <summary>
    /// Lines that have no diagnostic value but tend to trigger the Azure OpenAI
    /// jailbreak / content-filter. Matched case-insensitively against individual lines.
    /// </summary>
    private static readonly Regex[] _redactPatterns =
    [
        // Explicit prompt-injection phrases
        new(@"ignore\s+(all\s+)?(previous|prior|above|earlier)\s+(instructions?|prompts?|context|rules?)", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@"(you\s+are\s+now|act\s+as|pretend\s+(to\s+be|you\s+are)|roleplay)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@"(system\s+prompt|initial\s+prompt|forget\s+your\s+(instructions?|training))", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@"(jailbreak|DAN\b|do\s+anything\s+now)", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@"(disregard|override|bypass|circumvent)\s+(all\s+)?(your\s+)?(safety|restrictions?|guidelines?|policies?|rules?)", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        // XSS / HTML-injection payloads in test data
        new(@"<script[^>]*>", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@"\bon(error|load|click|mouseover|focus)\s*=\s*[""'(]", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@"javascript\s*:\s*(void|alert|eval|document)\s*[\[(]", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        // Long base64 / hex data blobs (≥60 contiguous encoded chars) — pure data, no analysis value
        new(@"[A-Za-z0-9+/=]{60,}={0,2}(?:\s|$)", RegexOptions.IgnoreCase | RegexOptions.Compiled),
    ];

    /// <summary>Diagnostically relevant log lines: errors, stack traces, assertions, timeouts.</summary>
    private static readonly Regex _diagnosticPattern = new(
        @"error|fail|exception|assert|timeout|crash|abort|fatal|warning|warn|" +
        @"unable\s+to|could\s+not|unexpected|stack\s+trace|\bat\s+\w|" +
        @"FAILED|ERROR|WARN|ASSERT|NullReference|OutOfMemory|unhandled",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>Rewrites a notifications API PR url to its web url.</summary>
    private static readonly Regex _apiPullUrl =
        new(@"^https://api\.github\.com/repos/(.+)/pulls/(\d+)$", RegexOptions.Compiled);

    /// <summary>Characters allowed in a free-text search fragment passed to <c>gh</c>.</summary>
    private static readonly Regex _unsafeQueryChars = new(@"[^\w\-.]", RegexOptions.Compiled);

    public GitHubService(DiagnosticsLogger logger)
    {
        _logger = logger;
    }

    // ── GraphQL fragments ───────────────────────────────────────────────

    private const string MyPrsQuery = """
        query($q: String!, $cursor: String) {
          rateLimit { limit remaining resetAt }
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
                baseRefName
                headRefName
                labels(first: 20) { nodes { name } }
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
                latestOpinionatedReviews(first: 10) {
                  nodes {
                    author { login }
                  }
                }
                reviews(last: 20) {
                  nodes {
                    author { login }
                    state
                    submittedAt
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
          rateLimit { limit remaining resetAt }
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
                labels(first: 20) { nodes { name } }
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
          rateLimit { limit remaining resetAt }
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
                labels(first: 20) { nodes { name } }
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

    private const string ConvertPrToDraftMutation = """
        mutation($prId: ID!) {
          convertPullRequestToDraft(input: {pullRequestId: $prId}) {
            pullRequest {
              id
              isDraft
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
                allPrs.AddRange(ParseMyPrs(json));
                if (!json.TryGetProperty("data", out var d) ||
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
                allPrs.AddRange(
                    ParseReviewPrs(json)
                        .Where(p => p.BaseRefName.StartsWith("release/", StringComparison.OrdinalIgnoreCase)));
                if (!json.TryGetProperty("data", out var d) ||
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
                allPrs.AddRange(ParseReviewPrs(json, currentUsername));
                if (!json.TryGetProperty("data", out var d) ||
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
            allPrs.AddRange(ParseReviewPrs(json));
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
                    prUrl = _apiPullUrl.Replace(apiUrl, "https://github.com/$1/pull/$2");
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
    /// Conclusions that GitHub's statusCheckRollup treats as a failing result (drives CIState.Failure),
    /// beyond the literal "failure" conclusion — a run stuck at one of these looks failed in the UI
    /// but was previously invisible to "Rerun failed jobs" because only "failure" was matched.
    /// </summary>
    private static readonly string[] FailingRunConclusions =
        ["failure", "cancelled", "timed_out", "action_required", "startup_failure"];

    /// <summary>
    /// Returns the IDs of failed GitHub Actions workflow runs for the given commit SHA.
    /// </summary>
    public async Task<IReadOnlyList<long>> FetchFailedRunIdsAsync(string owner, string repo, string headSha)
    {
        if (!ValidateSlug(owner, "owner") || !ValidateSlug(repo, "repo") || !ValidateSha(headSha))
            return [];
        var jqSelect = string.Join(" or ", FailingRunConclusions.Select(c => $".conclusion==\"{c}\""));
        var (output, stderr, exitCode) = await RunGhAsync(
            "api", $"repos/{owner}/{repo}/actions/runs?head_sha={headSha}",
            "--jq", $".workflow_runs[] | select({jqSelect}) | .id");
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
    /// Fetches the failed job log output for a workflow run, sanitized and truncated to 4000 chars.
    /// Truncation keeps the TAIL of the log (where actual test errors appear) and drops the head
    /// (where setup/security-scanner output — the most likely content-filter triggers — lives).
    /// The log is streamed and only a bounded tail is ever held in memory: raw CI logs can be
    /// hundreds of megabytes, which would otherwise land on the large object heap.
    /// </summary>
    public async Task<string> FetchFailedLogAsync(string owner, string repo, long runId)
    {
        if (!ValidateSlug(owner, "owner") || !ValidateSlug(repo, "repo"))
            return "";

        var (tail, redacted, exitCode) = await RunGhStreamingTailAsync(
            LogTailBudgetChars,
            "run", "view", runId.ToString(), "--log-failed", "--repo", $"{owner}/{repo}");

        if (exitCode != 0 || string.IsNullOrWhiteSpace(tail))
            return "";

        if (redacted > 0)
            _logger.Info($"FetchFailedLogAsync: redacted {redacted} line(s) from log for {owner}/{repo} run {runId}.");

        const int MaxLength = 4000;
        return tail.Length <= MaxLength
            ? tail
            : "[beginning of log omitted]\n" + tail[^MaxLength..];
    }

    /// <summary>
    /// Removes or rewrites lines from a CI log that are likely to trigger Azure OpenAI's
    /// content filter or are pure metadata with no flakiness-analysis value.
    /// Returns the cleaned log and the number of lines that were redacted.
    /// </summary>
    internal static (string Sanitized, int Redacted) SanitizeLogForAI(string log)
    {
        var result = new System.Text.StringBuilder(log.Length);
        int redacted = 0;

        foreach (var rawLine in log.Split('\n'))
        {
            result.AppendLine(SanitizeLogLine(rawLine, ref redacted));
        }

        return (result.ToString(), redacted);
    }

    /// <summary>
    /// Strips metadata prefixes from a single log line and replaces it with a redaction
    /// marker when it matches a content-filter trigger. Increments <paramref name="redacted"/>.
    /// </summary>
    private static string SanitizeLogLine(string rawLine, ref int redacted)
    {
        // Strip metadata prefixes first (no semantic loss)
        var line = _timestampPrefix.Replace(rawLine, "");
        line = _ansiEscape.Replace(line, "");

        foreach (var pattern in _redactPatterns)
        {
            if (pattern.IsMatch(line))
            {
                redacted++;
                return "[line redacted]";
            }
        }

        return line;
    }

    /// <summary>
    /// Extracts only the diagnostically relevant lines from a CI log: error messages,
    /// exception stack traces, assertion failures, and timeout/crash indicators.
    /// Used as a fallback excerpt when the full log triggers the Azure OpenAI content filter.
    /// Returns the original log unchanged when fewer than 3 diagnostic lines are found.
    /// </summary>
    internal static string ExtractErrorLines(string log, int maxLength = 2000)
    {
        var lines = log.Split('\n');
        var kept = new List<string>(capacity: lines.Length / 4);
        string? lastLine = null;

        // If fewer than 3 lines match the diagnostic pattern at all, the filter cannot
        // meaningfully reduce the log — return unchanged so the caller gets the full context.
        var rawMatchCount = lines.Count(l => _diagnosticPattern.IsMatch(l.TrimEnd('\r')));
        if (rawMatchCount < 3)
            return log;

        foreach (var line in lines)
        {
            var trimmed = line.TrimEnd('\r');
            // Skip consecutive duplicate lines (common in test runners)
            if (trimmed == lastLine)
                continue;
            if (_diagnosticPattern.IsMatch(trimmed))
            {
                kept.Add(trimmed);
                lastLine = trimmed;
            }
        }

        var joined = string.Join('\n', kept);
        return joined.Length <= maxLength
            ? joined
            : "[beginning of error lines omitted]\n" + joined[^maxLength..];
    }

    /// <summary>
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
    /// Reruns one GitHub Actions job (and whatever depends on it), leaving the rest of the
    /// workflow run alone — unlike <see cref="RerunFailedJobsAsync"/>, which restarts every
    /// failed job of a run.
    /// </summary>
    public async Task<(bool Ok, string? Error)> RerunJobAsync(string owner, string repo, long jobId)
    {
        if (!ValidateSlug(owner, "owner") || !ValidateSlug(repo, "repo") || jobId <= 0)
            return (false, "Invalid repository or job id.");

        var (_, stderr, exitCode) = await RunGhAsync(
            "api", "--method", "POST", $"repos/{owner}/{repo}/actions/jobs/{jobId}/rerun");

        if (exitCode == 0)
            return (true, null);

        var error = stderr?.Trim();
        _logger.Warn($"RerunJobAsync failed (exit={exitCode}) for job {jobId} in {owner}/{repo}: {error}");
        return (false, string.IsNullOrWhiteSpace(error) ? "GitHub refused the rerun request." : error);
    }

    /// <summary>
    /// Fetches all members of the given organizations via GraphQL (includes display name).
    /// Returns a deduplicated list of (Login, Name) pairs — unfiltered.
    /// Intended to be called once by the caller and cached; use <see cref="FilterOrgMembers"/> to search.
    /// </summary>
    public async Task<List<(string Login, string? Name)>> FetchOrgMembersAsync(IReadOnlyList<string> orgs)
    {
        var results = new List<(string Login, string? Name)>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        const string OrgMembersQuery = """
            query($org: String!, $cursor: String) {
              organization(login: $org) {
                membersWithRole(first: 100, after: $cursor) {
                  pageInfo { hasNextPage endCursor }
                  nodes { login name }
                }
              }
            }
            """;

        foreach (var org in orgs)
        {
            if (!ValidateSlug(org, "org"))
                continue;

            string? cursor = null;
            while (true)
            {
                var ghArgs = cursor is null
                    ? new[] { "api", "graphql", "-f", $"query={OrgMembersQuery}", "-f", $"org={org}" }
                    : new[] { "api", "graphql", "-f", $"query={OrgMembersQuery}", "-f", $"org={org}", "-f", $"cursor={cursor}" };

                var (stdout, stderr, exitCode) = await RunGhAsync(ghArgs);
                if (exitCode != 0)
                {
                    _logger.Warn($"FetchOrgMembersAsync: GraphQL failed for '{org}' (exit={exitCode}): {stderr?.Trim()}");
                    break;
                }
                if (string.IsNullOrWhiteSpace(stdout))
                    break;

                bool hasNextPage = false;
                try
                {
                    using var doc = JsonDocument.Parse(stdout);
                    var root = doc.RootElement;
                    if (!root.TryGetProperty("data", out var data)
                        || !data.TryGetProperty("organization", out var orgEl)
                        || orgEl.ValueKind == JsonValueKind.Null
                        || !orgEl.TryGetProperty("membersWithRole", out var members))
                    {
                        if (root.TryGetProperty("errors", out var errs))
                            _logger.Warn($"FetchOrgMembersAsync: GraphQL errors for org '{org}': {errs}");
                        break;
                    }

                    if (members.TryGetProperty("pageInfo", out var pageInfo))
                    {
                        hasNextPage = pageInfo.TryGetProperty("hasNextPage", out var hnp) && hnp.GetBoolean();
                        cursor = pageInfo.TryGetProperty("endCursor", out var ec) ? ec.GetString() : null;
                    }

                    if (members.TryGetProperty("nodes", out var nodes))
                    {
                        foreach (var node in nodes.EnumerateArray())
                        {
                            var login = node.TryGetProperty("login", out var lp) ? lp.GetString() : null;
                            var name = node.TryGetProperty("name", out var np) && np.ValueKind != JsonValueKind.Null
                                ? np.GetString() : null;
                            if (!string.IsNullOrWhiteSpace(login) && seen.Add(login!))
                                results.Add((login!, name));
                        }
                    }

                    if (!hasNextPage || cursor is null)
                        break;
                }
                catch (Exception ex)
                {
                    _logger.Warn($"FetchOrgMembersAsync: failed to parse response for '{org}': {ex.Message}");
                    break;
                }
            }
        }

        return results;
    }

    /// <summary>
    /// Filters a pre-fetched member list client-side against <paramref name="query"/>.
    /// Matches on login and display name (case-insensitive). Returns up to 10 results.
    /// </summary>
    public static List<(string Login, string? Name)> FilterOrgMembers(
        IReadOnlyList<(string Login, string? Name)> members, string query)
    {
        if (string.IsNullOrWhiteSpace(query))
            return [];
        var q = query.Trim();
        return members
            .Where(m => m.Login.Contains(q, StringComparison.OrdinalIgnoreCase)
                     || (m.Name is not null && m.Name.Contains(q, StringComparison.OrdinalIgnoreCase)))
            .Take(10)
            .ToList();
    }

    /// <summary>
    /// Searches GitHub users matching <paramref name="query"/> within the given organizations.
    /// For org-scoped searches, prefer <see cref="FetchOrgMembersAsync"/> + <see cref="FilterOrgMembers"/>
    /// to avoid re-fetching on every keystroke.
    /// When no orgs are configured, falls back to the GitHub global user search API.
    /// Returns up to 10 deduplicated (Login, Name) pairs.
    /// </summary>
    public async Task<List<(string Login, string? Name)>> SearchUsersAsync(string query, IReadOnlyList<string> orgs)
    {
        if (string.IsNullOrWhiteSpace(query))
            return [];

        if (orgs.Count > 0)
        {
            var all = await FetchOrgMembersAsync(orgs);
            return FilterOrgMembers(all, query);
        }

        // No org configured: use GitHub's global user search
        var safeQuery = _unsafeQueryChars.Replace(query.Trim(), "");
        if (string.IsNullOrEmpty(safeQuery))
            return [];

        var results = new List<(string Login, string? Name)>();
        var (stdout, stderr, exitCode) = await RunGhAsync(
            "api", $"search/users?q={safeQuery}&per_page=10",
            "-q", "[.items[] | {login, name}]");
        if (exitCode != 0)
        {
            _logger.Warn($"SearchUsersAsync: global search failed (exit={exitCode}): {stderr?.Trim()}");
            return results;
        }
        if (!string.IsNullOrWhiteSpace(stdout))
        {
            try
            {
                using var doc = JsonDocument.Parse(stdout);
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var el in doc.RootElement.EnumerateArray())
                {
                    var login = el.TryGetProperty("login", out var lp) ? lp.GetString() : null;
                    var name = el.TryGetProperty("name", out var np) ? np.GetString() : null;
                    if (!string.IsNullOrWhiteSpace(login) && seen.Add(login!))
                        results.Add((login!, name));
                }
            }
            catch (Exception ex)
            {
                _logger.Warn($"SearchUsersAsync: failed to parse global search response: {ex.Message}");
            }
        }
        return results.Take(10).ToList();
    }

    /// <summary>
    /// Requests review from the given GitHub login for a pull request.
    /// Returns true on success.
    /// </summary>
    public async Task<bool> RequestReviewerAsync(string owner, string repo, int prNumber, string login)
    {
        if (!ValidateSlug(owner, "owner") || !ValidateSlug(repo, "repo") || !ValidateSlug(login, "login"))
            return false;
        var (_, stderr, exitCode) = await RunGhAsync(
            "api",
            $"repos/{owner}/{repo}/pulls/{prNumber}/requested_reviewers",
            "--method", "POST",
            "-f", $"reviewers[]={login}");
        if (exitCode != 0)
            _logger.Warn($"RequestReviewerAsync failed (exit={exitCode}) for {owner}/{repo}#{prNumber} reviewer={login}: {stderr?.Trim()}");
        return exitCode == 0;
    }

    /// <summary>
    /// Removes a review request from the given GitHub login for a pull request.
    /// Returns true on success.
    /// </summary>
    public async Task<bool> RemoveReviewerAsync(string owner, string repo, int prNumber, string login)
    {
        if (!ValidateSlug(owner, "owner") || !ValidateSlug(repo, "repo") || !ValidateSlug(login, "login"))
            return false;
        var (_, stderr, exitCode) = await RunGhAsync(
            "api",
            $"repos/{owner}/{repo}/pulls/{prNumber}/requested_reviewers",
            "--method", "DELETE",
            "-f", $"reviewers[]={login}");
        if (exitCode != 0)
            _logger.Warn($"RemoveReviewerAsync failed (exit={exitCode}) for {owner}/{repo}#{prNumber} reviewer={login}: {stderr?.Trim()}");
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
    /// Uses GraphQL ConvertPullRequestToDraft mutation (CLI flag --draft doesn't exist in older gh versions).
    /// </summary>
    public async Task<bool> SetPrDraftAsync(string owner, string repo, int prNumber)
    {
        if (!ValidateSlug(owner, "owner") || !ValidateSlug(repo, "repo"))
            return false;

        try
        {
            // Step 1: Get the PR's GraphQL node ID via REST API (avoids GraphQL Int variable type issues)
            var (output, stderr, exitCode) = await RunGhAsync(
                "api", $"repos/{owner}/{repo}/pulls/{prNumber}",
                "--jq", ".node_id");

            if (exitCode != 0 || string.IsNullOrWhiteSpace(output))
            {
                _logger.Warn($"SetPrDraftAsync failed to fetch PR node ID (exit={exitCode}) for {owner}/{repo}#{prNumber}: {stderr?.Trim()}");
                return false;
            }

            var prId = output.Trim();
            if (string.IsNullOrWhiteSpace(prId))
            {
                _logger.Warn($"SetPrDraftAsync: empty PR node ID for {owner}/{repo}#{prNumber}");
                return false;
            }

            // Step 2: Run the ConvertPullRequestToDraft mutation
            var (mutOutput, mutStderr, mutExitCode) = await RunGhAsync(
                "api", "graphql",
                "-f", $"query={ConvertPrToDraftMutation}",
                "-f", $"prId={prId}");

            if (mutExitCode != 0)
            {
                _logger.Warn($"SetPrDraftAsync mutation failed (exit={mutExitCode}) for {owner}/{repo}#{prNumber}: {mutStderr?.Trim()}");
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            _logger.Error($"SetPrDraftAsync failed for {owner}/{repo}#{prNumber}", ex);
            return false;
        }
    }

    /// <summary>
    /// Enables auto-merge (squash) on a PR. Returns true on success.
    /// </summary>
    public async Task<bool> EnableAutoMergeAsync(string owner, string repo, int prNumber, string mergeMethod = "merge")
    {
        if (!ValidateSlug(owner, "owner") || !ValidateSlug(repo, "repo"))
            return false;
        var methodFlag = mergeMethod switch
        {
            "squash" => "--squash",
            "rebase" => "--rebase",
            _        => "--merge",
        };
        var (_, stderr, exitCode) = await RunGhAsync("pr", "merge", prNumber.ToString(), "--auto", methodFlag, "--repo", $"{owner}/{repo}");
        if (exitCode != 0)
            _logger.Warn($"EnableAutoMergeAsync failed (exit={exitCode}) for {owner}/{repo}#{prNumber}: {stderr?.Trim()}");
        return exitCode == 0;
    }

    /// <summary>
    /// Disables auto-merge on a PR. Returns true on success.
    /// </summary>
    public async Task<bool> DisableAutoMergeAsync(string owner, string repo, int prNumber)
    {
        if (!ValidateSlug(owner, "owner") || !ValidateSlug(repo, "repo"))
            return false;
        var (_, stderr, exitCode) = await RunGhAsync("pr", "merge", prNumber.ToString(), "--disable-auto", "--repo", $"{owner}/{repo}");
        if (exitCode != 0)
            _logger.Warn($"DisableAutoMergeAsync failed (exit={exitCode}) for {owner}/{repo}#{prNumber}: {stderr?.Trim()}");
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

    /// <summary>Number of attempts for a single GraphQL page before the poll is failed.</summary>
    private const int GraphQlMaxAttempts = 3;

    private static readonly TimeSpan[] GraphQlRetryDelays =
    [
        TimeSpan.FromSeconds(2),
        TimeSpan.FromSeconds(5),
    ];

    /// <summary>
    /// Runs a GraphQL search query. Always either returns a usable response or throws
    /// <see cref="GitHubApiException"/> — never a silently empty result, because callers
    /// cannot distinguish "no PRs" from "the call failed".
    /// </summary>
    private async Task<JsonElement> RunGraphQlAsync(string query, string searchString, string? cursor = null)
    {
        var ghArgs = cursor is null
            ? new[] { "api", "graphql", "-f", $"query={query}", "-f", $"q={searchString}" }
            : new[] { "api", "graphql", "-f", $"query={query}", "-f", $"q={searchString}", "-f", $"cursor={cursor}" };

        for (int attempt = 1; ; attempt++)
        {
            bool lastAttempt = attempt >= GraphQlMaxAttempts;
            try
            {
                return await RunGraphQlOnceAsync(ghArgs, searchString);
            }
            catch (GitHubApiException ex) when (!lastAttempt)
            {
                _logger.Warn($"GitHubService GraphQL attempt {attempt}/{GraphQlMaxAttempts} failed for query '{searchString}': {ex.Message}");
            }

            await Task.Delay(GraphQlRetryDelays[Math.Min(attempt - 1, GraphQlRetryDelays.Length - 1)]);
        }
    }

    private async Task<JsonElement> RunGraphQlOnceAsync(string[] ghArgs, string searchString)
    {
        var (output, stderr, exitCode) = await RunGhAsync(ghArgs);
        if (exitCode != 0)
            throw new GitHubApiException($"gh api graphql failed (exit={exitCode}) for query '{searchString}': {stderr?.Trim()}");

        if (string.IsNullOrWhiteSpace(output))
            throw new GitHubApiException($"gh api graphql returned empty output for query '{searchString}'.");

        JsonElement root;
        try
        {
            using var doc = JsonDocument.Parse(output);
            // Clone so we can dispose the document
            root = doc.RootElement.Clone();
        }
        catch (JsonException ex)
        {
            throw new GitHubApiException($"gh api graphql returned unparseable JSON for query '{searchString}'.", ex);
        }

        // Every search query asks for rateLimit, so polling keeps the shared budget current.
        RecordRateLimit(root);

        if (root.TryGetProperty("errors", out var errors) && errors.ValueKind == JsonValueKind.Array)
        {
            // A partial response still carries usable data; a fully failed one (rate limit,
            // outage) does not — and must never be treated as "this user has no PRs".
            if (!HasSearchNodes(root))
                throw new GitHubApiException($"gh api graphql returned errors without data for query '{searchString}': {errors}");

            _logger.Warn($"GitHubService GraphQL response contains errors for query '{searchString}': {errors}");
        }
        else if (!HasSearchNodes(root))
        {
            throw new GitHubApiException($"gh api graphql response is missing data.search.nodes for query '{searchString}'.");
        }

        return root;
    }

    private static bool HasSearchNodes(JsonElement root) =>
        root.TryGetProperty("data", out var data)
        && data.ValueKind == JsonValueKind.Object
        && data.TryGetProperty("search", out var search)
        && search.ValueKind == JsonValueKind.Object
        && search.TryGetProperty("nodes", out var nodes)
        && nodes.ValueKind == JsonValueKind.Array;

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

            string? mergeableValueMyPr = node.TryGetProperty("mergeable", out var mergeableMyPr)
                ? mergeableMyPr.GetString()
                : null;
            bool hasConflictsMyPr = mergeableValueMyPr == "CONFLICTING";
            bool isMergeabilityUnknownMyPr = mergeableValueMyPr == "UNKNOWN";

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
                BaseRefName = node.TryGetProperty("baseRefName", out var brn1)
                    ? brn1.GetString() ?? ""
                    : "",
                HeadRefName = node.TryGetProperty("headRefName", out var hrn1)
                    ? hrn1.GetString() ?? ""
                    : "",
                HeadCommitSha = GetCommitOid(node),
                CIState = ciState,
                HasConflicts = hasConflictsMyPr,
                IsMergeabilityUnknown = isMergeabilityUnknownMyPr,
                IsApproved = node.TryGetProperty("reviewDecision", out var rd1)
                    && rd1.GetString() == "APPROVED",
                UnresolvedReviewCommentCount = ParseUnresolvedReviewCommentCount(node),
                ReviewerLogins = ParseReviewerLogins(node),
                TeamReviewerSlugs = ParseTeamReviewerSlugs(node),
                ReviewerStates = ParseReviewerStates(node),
                Labels = ParseLabels(node),
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

            string? mergeableValueReview = node.TryGetProperty("mergeable", out var mergeableReview)
                ? mergeableReview.GetString()
                : null;
            bool hasConflictsReview = mergeableValueReview == "CONFLICTING";
            bool isMergeabilityUnknownReview = mergeableValueReview == "UNKNOWN";

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
                HasConflicts = hasConflictsReview,
                IsMergeabilityUnknown = isMergeabilityUnknownReview,
                IsApproved = node.TryGetProperty("reviewDecision", out var rd2)
                    && rd2.GetString() == "APPROVED",
                UnresolvedReviewCommentCount = ParseUnresolvedReviewCommentCount(node),
                ReviewerLogins = ParseReviewerLogins(node),
                TeamReviewerSlugs = ParseTeamReviewerSlugs(node),
                IsTeamReviewRequested = isTeamOnly,
                Labels = ParseLabels(node),
            });
        }

        return result;
    }

    internal static IReadOnlyList<string> ParseLabels(JsonElement node)
    {
        if (!node.TryGetProperty("labels", out var labels)) return [];
        if (labels.ValueKind != JsonValueKind.Object) return [];
        if (!labels.TryGetProperty("nodes", out var nodes)) return [];
        if (nodes.ValueKind != JsonValueKind.Array) return [];

        var names = new List<string>();
        foreach (var labelNode in nodes.EnumerateArray())
        {
            if (labelNode.ValueKind == JsonValueKind.Object
                && labelNode.TryGetProperty("name", out var name)
                && name.ValueKind == JsonValueKind.String
                && name.GetString() is { Length: > 0 } value
                && !names.Contains(value, StringComparer.OrdinalIgnoreCase))
                names.Add(value);
        }
        return names;
    }

    internal static IReadOnlyList<string> ParseReviewerLogins(JsonElement node)
    {
        if (!node.TryGetProperty("reviewRequests", out var reviewRequests)) return [];
        if (reviewRequests.ValueKind != JsonValueKind.Object) return [];
        if (!reviewRequests.TryGetProperty("nodes", out var nodes)) return [];
        if (nodes.ValueKind != JsonValueKind.Array) return [];

        var loginsSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
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
                loginsSet.Add(loginStr);
            }
            else if (type == "Team")
            {
                if (!reviewer.TryGetProperty("slug", out var slug)) continue;
                var slugStr = slug.GetString() ?? "";
                if (!string.IsNullOrEmpty(slugStr))
                    loginsSet.Add(slugStr);
            }
        }

        // Also include reviewers who have already submitted a review (they are removed from reviewRequests once done).
        if (node.TryGetProperty("latestOpinionatedReviews", out var latestReviews)
            && latestReviews.ValueKind == JsonValueKind.Object
            && latestReviews.TryGetProperty("nodes", out var reviewNodes)
            && reviewNodes.ValueKind == JsonValueKind.Array)
        {
            foreach (var reviewNode in reviewNodes.EnumerateArray())
            {
                if (!reviewNode.TryGetProperty("author", out var author)) continue;
                if (!author.TryGetProperty("login", out var login)) continue;
                var loginStr = login.GetString() ?? "";
                if (string.IsNullOrEmpty(loginStr)) continue;
                if (loginStr.StartsWith("copilot", StringComparison.OrdinalIgnoreCase)) continue;
                loginsSet.Add(loginStr);
            }
        }

        return [.. loginsSet];
    }

    /// <summary>Team slugs among the pending review requests (CODEOWNERS teams are auto-requested).</summary>
    internal static IReadOnlyList<string> ParseTeamReviewerSlugs(JsonElement node)
    {
        if (!node.TryGetProperty("reviewRequests", out var reviewRequests)) return [];
        if (reviewRequests.ValueKind != JsonValueKind.Object) return [];
        if (!reviewRequests.TryGetProperty("nodes", out var nodes)) return [];
        if (nodes.ValueKind != JsonValueKind.Array) return [];

        var slugs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var requestNode in nodes.EnumerateArray())
        {
            if (!requestNode.TryGetProperty("requestedReviewer", out var reviewer)) continue;
            if (!reviewer.TryGetProperty("__typename", out var typename)) continue;
            if (typename.GetString() != "Team") continue;
            if (!reviewer.TryGetProperty("slug", out var slug)) continue;
            var slugStr = slug.GetString() ?? "";
            if (!string.IsNullOrEmpty(slugStr))
                slugs.Add(slugStr);
        }

        return [.. slugs];
    }

    internal static IReadOnlyDictionary<string, ReviewState> ParseReviewerStates(JsonElement node)
    {
        var states = new Dictionary<string, ReviewState>(StringComparer.OrdinalIgnoreCase);

        // Pass 1: latest non-dismissed submitted review per author from `reviews`.
        if (node.TryGetProperty("reviews", out var reviews)
            && reviews.ValueKind == JsonValueKind.Object
            && reviews.TryGetProperty("nodes", out var reviewNodes)
            && reviewNodes.ValueKind == JsonValueKind.Array)
        {
            var latestByAuthor = new Dictionary<string, (DateTimeOffset SubmittedAt, string State)>(StringComparer.OrdinalIgnoreCase);
            foreach (var reviewNode in reviewNodes.EnumerateArray())
            {
                if (!reviewNode.TryGetProperty("author", out var author) || author.ValueKind != JsonValueKind.Object) continue;
                if (!author.TryGetProperty("login", out var loginEl)) continue;
                var login = loginEl.GetString();
                if (string.IsNullOrEmpty(login)) continue;
                if (login.StartsWith("copilot", StringComparison.OrdinalIgnoreCase)) continue;

                if (!reviewNode.TryGetProperty("state", out var stateEl)) continue;
                var state = stateEl.GetString() ?? "";
                if (state is "" or "DISMISSED" or "PENDING") continue;

                var submittedAt = reviewNode.TryGetProperty("submittedAt", out var saEl)
                    && DateTimeOffset.TryParse(saEl.GetString(), out var parsedSa)
                    ? parsedSa
                    : DateTimeOffset.MinValue;

                if (!latestByAuthor.TryGetValue(login, out var existing) || submittedAt >= existing.SubmittedAt)
                    latestByAuthor[login] = (submittedAt, state);
            }

            foreach (var (login, value) in latestByAuthor)
            {
                states[login] = value.State switch
                {
                    "APPROVED" => ReviewState.Approved,
                    "CHANGES_REQUESTED" => ReviewState.ChangesRequested,
                    "COMMENTED" => ReviewState.Commented,
                    _ => ReviewState.Pending,
                };
            }
        }

        // Pass 2: an active pending review request always overrides to Pending,
        // even if the reviewer has an older non-dismissed review (fresh request awaiting a new response).
        if (node.TryGetProperty("reviewRequests", out var reviewRequests)
            && reviewRequests.ValueKind == JsonValueKind.Object
            && reviewRequests.TryGetProperty("nodes", out var requestNodes)
            && requestNodes.ValueKind == JsonValueKind.Array)
        {
            foreach (var requestNode in requestNodes.EnumerateArray())
            {
                if (!requestNode.TryGetProperty("requestedReviewer", out var reviewer)) continue;
                if (!reviewer.TryGetProperty("__typename", out var typename)) continue;

                var type = typename.GetString();
                string? login = null;
                if (type == "User" && reviewer.TryGetProperty("login", out var loginEl))
                    login = loginEl.GetString();
                else if (type == "Team" && reviewer.TryGetProperty("slug", out var slugEl))
                    login = slugEl.GetString();

                if (string.IsNullOrEmpty(login)) continue;
                if (login.StartsWith("copilot", StringComparison.OrdinalIgnoreCase)) continue;

                states[login] = ReviewState.Pending;
            }
        }

        return states;
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

    // ── CI checks for a single PR ───────────────────────────────────────

    private const string PrChecksQuery = """
        query($owner: String!, $repo: String!, $number: Int!) {
          rateLimit { limit remaining resetAt }
          repository(owner: $owner, name: $repo) {
            pullRequest(number: $number) {
              commits(last: 1) {
                nodes {
                  commit {
                    oid
                    statusCheckRollup {
                      state
                      contexts(first: 100) {
                        nodes {
                          __typename
                          ... on CheckRun {
                            databaseId
                            name
                            status
                            conclusion
                            startedAt
                            completedAt
                            detailsUrl
                            checkSuite {
                              workflowRun {
                                databaseId
                                event
                                workflow { name }
                              }
                            }
                          }
                          ... on StatusContext {
                            context
                            state
                            createdAt
                            targetUrl
                          }
                        }
                      }
                    }
                  }
                }
              }
            }
          }
        }
        """;

    /// <summary>
    /// Fetches every CI check on the PR's latest commit, so the user can see which jobs run,
    /// which passed and which failed without clicking through to GitHub.
    /// Never throws: a failed call comes back as <see cref="CheckFetchStatus.Failed"/> or
    /// <see cref="CheckFetchStatus.RateLimited"/>, which is what stops the panel's auto-refresh.
    /// </summary>
    public async Task<CheckFetchResult> FetchPrChecksAsync(string owner, string repo, int prNumber)
    {
        if (!ValidateSlug(owner, "owner") || !ValidateSlug(repo, "repo") || prNumber <= 0)
            return CheckFetchResult.Failure();

        var (output, stderr, exitCode) = await RunGhAsync(
            "api", "graphql",
            "-f", $"query={PrChecksQuery}",
            "-F", $"owner={owner}",
            "-F", $"repo={repo}",
            "-F", $"number={prNumber}");

        if (exitCode != 0 || string.IsNullOrWhiteSpace(output))
        {
            // gh reports a rate limit on stderr, but a GraphQL rate-limit error arrives as a
            // well-formed body on a non-zero exit — so both streams have to be inspected.
            bool rateLimited = LooksRateLimited(stderr) || LooksRateLimited(output);
            _logger.Warn($"FetchPrChecksAsync {(rateLimited ? "hit a rate limit" : "failed")} (exit={exitCode}) for {owner}/{repo}#{prNumber}: {stderr?.Trim()}");
            return rateLimited ? CheckFetchResult.RateLimited() : CheckFetchResult.Failure();
        }

        try
        {
            using var doc = JsonDocument.Parse(output);
            var root = doc.RootElement;
            RecordRateLimit(root);
            var (remaining, resetAt) = ParseRateLimitBudget(root);

            if (HasRateLimitError(root))
            {
                _logger.Warn($"FetchPrChecksAsync hit a GraphQL rate limit for {owner}/{repo}#{prNumber}; resets at {resetAt?.ToString("u") ?? "unknown"}.");
                return CheckFetchResult.RateLimited(resetAt);
            }

            return CheckFetchResult.Success(ParsePrChecks(root), remaining, resetAt);
        }
        catch (JsonException ex)
        {
            _logger.Warn($"FetchPrChecksAsync returned unparseable JSON for {owner}/{repo}#{prNumber}: {ex.Message}");
            return CheckFetchResult.Failure();
        }
    }

    /// <summary>Text GitHub and <c>gh</c> use when a primary, secondary or abuse rate limit is hit.</summary>
    private static readonly string[] RateLimitMarkers =
    [
        "rate limit exceeded",
        "secondary rate limit",
        "rate_limited",
        "abuse detection",
        "retry-after",
    ];

    /// <summary>Whether a <c>gh</c> stream mentions a rate limit. Case-insensitive, cheap enough for one call.</summary>
    internal static bool LooksRateLimited(string? text) =>
        !string.IsNullOrEmpty(text)
        && RateLimitMarkers.Any(m => text.Contains(m, StringComparison.OrdinalIgnoreCase));

    /// <summary>Whether a parsed GraphQL response carries a RATE_LIMITED error entry.</summary>
    internal static bool HasRateLimitError(JsonElement root)
    {
        if (!root.TryGetProperty("errors", out var errors) || errors.ValueKind != JsonValueKind.Array)
            return false;

        foreach (var error in errors.EnumerateArray())
        {
            if (error.ValueKind != JsonValueKind.Object)
                continue;
            if (LooksRateLimited(GetStringOrNull(error, "type")) || LooksRateLimited(GetStringOrNull(error, "message")))
                return true;
        }
        return false;
    }

    /// <summary>
    /// Reads <c>data.rateLimit</c> — the remaining GraphQL points and when the window resets.
    /// Requesting this field costs nothing, and it is what lets the panel back off *before*
    /// GitHub starts refusing calls.
    /// </summary>
    internal static (int? Remaining, DateTimeOffset? ResetAt) ParseRateLimitBudget(JsonElement root)
    {
        if (!TryGetRateLimit(root, out var rateLimit))
            return (null, null);

        int? remaining = rateLimit.TryGetProperty("remaining", out var r) && r.ValueKind == JsonValueKind.Number
            ? r.GetInt32()
            : null;

        return (remaining, GetDateOrNull(rateLimit, "resetAt"));
    }

    private static bool TryGetRateLimit(JsonElement root, out JsonElement rateLimit)
    {
        rateLimit = default;
        if (!root.TryGetProperty("data", out var data)
            || data.ValueKind != JsonValueKind.Object
            || !data.TryGetProperty("rateLimit", out var node)
            || node.ValueKind != JsonValueKind.Object)
            return false;

        rateLimit = node;
        return true;
    }

    /// <summary>Projects <c>data.rateLimit</c> onto a snapshot, or null when the field is absent.</summary>
    internal static RateLimitSnapshot? ParseRateLimitSnapshot(JsonElement root, DateTimeOffset observedAt)
    {
        if (!TryGetRateLimit(root, out var rateLimit)
            || !rateLimit.TryGetProperty("remaining", out var r)
            || r.ValueKind != JsonValueKind.Number)
            return null;

        int limit = rateLimit.TryGetProperty("limit", out var l) && l.ValueKind == JsonValueKind.Number
            ? l.GetInt32()
            : 0;

        return new RateLimitSnapshot(r.GetInt32(), limit, GetDateOrNull(rateLimit, "resetAt"), observedAt);
    }

    // ── Shared rate-limit budget ────────────────────────────────────────

    /// <summary>Points below which the budget is logged as a warning, once per window.</summary>
    private const int LowBudgetWarningThreshold = 500;

    private volatile RateLimitSnapshot? _lastRateLimit;
    private DateTimeOffset _lastLowBudgetWarning = DateTimeOffset.MinValue;

    /// <summary>
    /// What the most recent GraphQL call reported about the rate-limit budget, or null before the
    /// first one. Every query asks for it, so polling alone keeps this current — which is what
    /// lets the checks panel pace itself without spending a call to find out.
    /// </summary>
    public RateLimitSnapshot? LastRateLimit => _lastRateLimit;

    /// <summary>Records the budget of a parsed response and warns when it starts running thin.</summary>
    private void RecordRateLimit(JsonElement root)
    {
        var snapshot = ParseRateLimitSnapshot(root, DateTimeOffset.UtcNow);
        if (snapshot is null)
            return;

        _lastRateLimit = snapshot;

        // One warning per reset window: a thin budget stays thin for a while, and the log is
        // read after the fact, so repeating it every poll would bury everything else.
        if (snapshot.Remaining < LowBudgetWarningThreshold
            && snapshot.ResetAt is { } reset
            && reset > _lastLowBudgetWarning)
        {
            _lastLowBudgetWarning = reset;
            _logger.Warn($"GitHub GraphQL rate limit is running low: {snapshot}.");
        }
    }

    /// <summary>
    /// Projects the <c>statusCheckRollup.contexts</c> nodes of <see cref="PrChecksQuery"/> onto
    /// <see cref="CheckRunInfo"/>, keeping both modern check runs and legacy status contexts.
    /// </summary>
    internal static List<CheckRunInfo> ParsePrChecks(JsonElement root)
    {
        var result = new List<CheckRunInfo>();

        if (!root.TryGetProperty("data", out var data)
            || !data.TryGetProperty("repository", out var repository)
            || repository.ValueKind != JsonValueKind.Object
            || !repository.TryGetProperty("pullRequest", out var pr)
            || pr.ValueKind != JsonValueKind.Object
            || !pr.TryGetProperty("commits", out var commits)
            || !commits.TryGetProperty("nodes", out var commitNodes)
            || commitNodes.ValueKind != JsonValueKind.Array)
            return result;

        foreach (var commitNode in commitNodes.EnumerateArray())
        {
            if (!commitNode.TryGetProperty("commit", out var commit)
                || !commit.TryGetProperty("statusCheckRollup", out var rollup)
                || rollup.ValueKind != JsonValueKind.Object
                || !rollup.TryGetProperty("contexts", out var contexts)
                || !contexts.TryGetProperty("nodes", out var contextNodes)
                || contextNodes.ValueKind != JsonValueKind.Array)
                continue;

            foreach (var node in contextNodes.EnumerateArray())
            {
                var check = ParseCheckNode(node);
                if (check is not null)
                    result.Add(check);
            }
        }

        return result;
    }

    private static CheckRunInfo? ParseCheckNode(JsonElement node)
    {
        if (node.ValueKind != JsonValueKind.Object)
            return null;

        var typeName = GetStringOrNull(node, "__typename");

        if (string.Equals(typeName, "StatusContext", StringComparison.Ordinal))
        {
            var context = GetStringOrNull(node, "context");
            if (string.IsNullOrWhiteSpace(context))
                return null;

            return new CheckRunInfo
            {
                Name = context,
                State = CheckRunInfo.FromStatusContext(GetStringOrNull(node, "state")),
                Url = GetStringOrNull(node, "targetUrl") ?? "",
                StartedAt = GetDateOrNull(node, "createdAt"),
            };
        }

        var name = GetStringOrNull(node, "name");
        if (string.IsNullOrWhiteSpace(name))
            return null;

        string workflowName = "";
        string triggerEvent = "";
        long workflowRunId = 0;
        if (node.TryGetProperty("checkSuite", out var suite)
            && suite.ValueKind == JsonValueKind.Object
            && suite.TryGetProperty("workflowRun", out var run)
            && run.ValueKind == JsonValueKind.Object)
        {
            if (run.TryGetProperty("workflow", out var workflow)
                && workflow.ValueKind == JsonValueKind.Object)
                workflowName = GetStringOrNull(workflow, "name") ?? "";

            triggerEvent = GetStringOrNull(run, "event") ?? "";

            if (run.TryGetProperty("databaseId", out var dbId) && dbId.ValueKind == JsonValueKind.Number)
                workflowRunId = dbId.GetInt64();
        }

        // For an Actions check run the check run's databaseId is the job id — the same number
        // as the /job/<id> segment of detailsUrl — which is what reruns a single job.
        long jobId = node.TryGetProperty("databaseId", out var jobDbId) && jobDbId.ValueKind == JsonValueKind.Number
            ? jobDbId.GetInt64()
            : 0;

        return new CheckRunInfo
        {
            Name = name,
            WorkflowName = workflowName,
            Event = triggerEvent,
            WorkflowRunId = workflowRunId,
            JobId = jobId,
            State = CheckRunInfo.FromCheckRun(GetStringOrNull(node, "status"), GetStringOrNull(node, "conclusion")),
            Url = GetStringOrNull(node, "detailsUrl") ?? "",
            StartedAt = GetDateOrNull(node, "startedAt"),
            CompletedAt = GetDateOrNull(node, "completedAt"),
        };
    }

    private static string? GetStringOrNull(JsonElement node, string property) =>
        node.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static DateTimeOffset? GetDateOrNull(JsonElement node, string property) =>
        node.TryGetProperty(property, out var value)
        && value.ValueKind == JsonValueKind.String
        && DateTimeOffset.TryParse(value.GetString(), out var parsed)
            ? parsed
            : null;

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

    /// <summary>Sanitized tail of a CI log kept in memory; only the last 4000 chars are ever used.</summary>
    private const int LogTailBudgetChars = 64 * 1024;

    /// <summary>Hard cap on characters read from a streamed subprocess before it is killed.</summary>
    private const int StreamingReadCapChars = 32 * 1024 * 1024;

    private static readonly TimeSpan GhTimeout = TimeSpan.FromMinutes(2);

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
                StandardOutputEncoding = System.Text.Encoding.UTF8,
                StandardErrorEncoding = System.Text.Encoding.UTF8,
            };
            foreach (var arg in arguments)
                process.StartInfo.ArgumentList.Add(arg);

            process.Start();
            using var timeout = new CancellationTokenSource(GhTimeout);
            var outputTask = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var stderrTask = process.StandardError.ReadToEndAsync(timeout.Token);

            string output, stderr;
            try
            {
                await process.WaitForExitAsync(timeout.Token);
                output = await outputTask;
                stderr = await stderrTask;
            }
            catch (OperationCanceledException)
            {
                KillProcessTree(process, arguments);
                return (null, null, -1);
            }

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

    /// <summary>
    /// Runs <c>gh</c> and streams stdout line by line, sanitizing each line immediately and
    /// retaining only the last <paramref name="tailBudgetChars"/> characters. Used for CI logs,
    /// which can be hundreds of megabytes while only their tail is of interest.
    /// </summary>
    private async Task<(string Tail, int Redacted, int ExitCode)> RunGhStreamingTailAsync(
        int tailBudgetChars, params string[] arguments)
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
                StandardOutputEncoding = System.Text.Encoding.UTF8,
                StandardErrorEncoding = System.Text.Encoding.UTF8,
            };
            foreach (var arg in arguments)
                process.StartInfo.ArgumentList.Add(arg);

            process.Start();
            using var timeout = new CancellationTokenSource(GhTimeout);

            // Drained but discarded: an unread stderr pipe would block the child process.
            var stderrTask = process.StandardError.ReadToEndAsync(timeout.Token);

            var tail = new Queue<string>();
            var tailChars = 0;
            var totalChars = 0;
            var redacted = 0;
            var capped = false;

            try
            {
                while (await process.StandardOutput.ReadLineAsync(timeout.Token) is { } rawLine)
                {
                    totalChars += rawLine.Length + 1;
                    if (totalChars > StreamingReadCapChars)
                    {
                        capped = true;
                        break;
                    }

                    var line = SanitizeLogLine(rawLine, ref redacted);
                    tail.Enqueue(line);
                    tailChars += line.Length + 1;

                    while (tailChars > tailBudgetChars && tail.Count > 1)
                        tailChars -= tail.Dequeue().Length + 1;
                }

                if (capped)
                {
                    _logger.Warn($"GitHubService: output cap reached for 'gh {string.Join(" ", arguments)}' — killing process.");
                    KillProcessTree(process, arguments);
                }
                else
                {
                    await process.WaitForExitAsync(timeout.Token);
                }
            }
            catch (OperationCanceledException)
            {
                KillProcessTree(process, arguments);
                return ("", redacted, -1);
            }

            var stderr = capped ? "" : await stderrTask;
            var exitCode = capped ? 0 : process.ExitCode;
            if (exitCode != 0)
                _logger.Error($"GitHubService gh command failed (exit={exitCode}). args: {string.Join(" ", arguments)}. stderr: {stderr?.Trim()}");

            return (string.Join(Environment.NewLine, tail), redacted, exitCode);
        }
        catch (Exception ex)
        {
            _logger.Error("GitHubService failed to stream gh process output.", ex);
            return ("", 0, -1);
        }
    }

    private void KillProcessTree(Process process, string[] arguments)
    {
        try
        {
            if (!process.HasExited)
            {
                _logger.Warn($"GitHubService: killing unresponsive 'gh {string.Join(" ", arguments)}'.");
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception ex)
        {
            _logger.Warn($"GitHubService: failed to kill gh process: {DiagnosticsLogger.SummarizeException(ex)}");
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
