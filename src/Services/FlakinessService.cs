using System.Text.RegularExpressions;
using PrMonitor.Models;
using PrMonitor.Settings;

namespace PrMonitor.Services;

/// <summary>
/// Listens for CI failures on the current user's PRs, checks local flakiness rules,
/// and — when no local rule matches — calls the Copilot API to determine flakiness.
/// Automatically reruns failed GitHub Actions (max attempts configurable per PR).
/// </summary>
public sealed class FlakinessService
{
    // Built-in infrastructure patterns that are always flaky — checked before user rules and AI.
    // These short-circuit the AI entirely to avoid false "real failure" classifications.
    private static readonly (string Pattern, string Description)[] _builtInRules =
    [
        (@"(?i)cannot create a symbolic link", "CI runner: cannot create symbolic link (Windows)"),
        (@"(?i)unable to create symlink", "CI runner: git unable to create symlink"),
        (@"(?i)(error.*creating.*symbolic\s*link|creating.*symbolic\s*link.*error)", "CI runner: I/O error creating symbolic link"),
        (@"(?i)symlink.*(failed|error|unable|permission\s+denied)", "CI runner: symlink operation failed"),
    ];

    private readonly GitHubService _github;
    private readonly IFlakinessAnalyzer _gitHubModels;
    private readonly IFlakinessAnalyzer _claude;
    private readonly AppSettings _settings;
    private readonly NotificationService _notifications;
    private readonly DiagnosticsLogger _logger;

    public FlakinessService(
        GitHubService github,
        IFlakinessAnalyzer gitHubModels,
        IFlakinessAnalyzer claude,
        AppSettings settings,
        NotificationService notifications,
        DiagnosticsLogger logger)
    {
        _github = github;
        _gitHubModels = gitHubModels;
        _claude = claude;
        _settings = settings;
        _notifications = notifications;
        _logger = logger;
    }

    /// <summary>Raised when an automatic flaky rerun is triggered. Arg: PR key ("owner/repo#n").</summary>
    public event Action<string>? FlakyRerunTriggered;

    /// <summary>Raised when a CI failure is classified as a real (non-flaky) failure. Arg: PR key.</summary>
    public event Action<string>? RealFailureClassified;

    public void Subscribe(PollingService polling)
    {
        // PrChanged fires on a thread-pool thread, but AppSettings belongs to the UI thread:
        // everything else mutates and saves it there, and Save() serialises the live
        // collections. So the handling runs on the context Subscribe was called on; only the
        // regex matching over the log goes back to the thread pool.
        var context = SynchronizationContext.Current;
        polling.PrChanged += (_, e) =>
        {
            if (context is null)
                OnPrChanged(e);
            else
                context.Post(_ => OnPrChanged(e), null);
        };
    }

    private void OnPrChanged(PrChangeEventArgs e)
    {
        if (e.Kind == PrChangeKind.CIStatusChanged
            && e.PullRequest.CIState == CIState.Failure
            && !e.PullRequest.IsDraft
            && !_settings.HiddenPrKeys.Contains(e.PullRequest.Key)
            && string.Equals(e.PullRequest.Author, _settings.GitHubUsername, StringComparison.OrdinalIgnoreCase))
        {
            _ = HandleCIFailureSafeAsync(e.PullRequest);
        }
    }

    /// <summary>Fire-and-forget wrapper: an exception here would otherwise go unobserved.</summary>
    private async Task HandleCIFailureSafeAsync(PullRequestInfo pr)
    {
        try
        {
            await HandleCIFailureAsync(pr);
        }
        catch (Exception ex)
        {
            _logger.Error($"FlakinessService: handling the CI failure of {pr.Key} failed.", ex);
        }
    }

    private async Task HandleCIFailureAsync(PullRequestInfo pr)
    {
        if (!_settings.FlakinessAnalysisEnabled)
            return;

        if (_settings.FlakinessAutoMergeOnly && !pr.HasAutoMerge)
        {
            _logger.Info($"FlakinessService: skipping {pr.Key} because auto-merge-only mode is enabled.");
            return;
        }

        var prKey = pr.Key;
        var maxAttempts = Math.Max(1, _settings.FlakinessMaxReruns);
        var rerunCount = GetRerunCount(prKey);
        if (rerunCount >= maxAttempts)
        {
            _logger.Info($"FlakinessService: max reruns ({maxAttempts}) reached for {prKey}, skipping.");
            return;
        }

        // Parse owner/repo from "owner/repo"
        var slashIdx = pr.Repository.IndexOf('/');
        if (slashIdx < 0 || string.IsNullOrEmpty(pr.HeadCommitSha))
        {
            _logger.Warn($"FlakinessService: cannot resolve owner/repo or commit SHA for {prKey}.");
            return;
        }
        var owner = pr.Repository[..slashIdx];
        var repo = pr.Repository[(slashIdx + 1)..];

        // Fetch failed run IDs
        var runIds = await _github.FetchFailedRunIdsAsync(owner, repo, pr.HeadCommitSha);
        if (runIds.Count == 0)
        {
            _logger.Info($"FlakinessService: no failed GitHub Actions runs found for {prKey} @ {pr.HeadCommitSha}. Skipping.");
            return;
        }

        var primaryRunId = runIds[0];

        // Fetch log
        var log = await _github.FetchFailedLogAsync(owner, repo, primaryRunId);

        // Build context
        var context = new FailureContext
        {
            Repository = pr.Repository,
            PrNumber = pr.Number,
            PrTitle = pr.Title,
            HeadBranch = pr.HeadRefName,
            HeadCommitSha = pr.HeadCommitSha,
            FailedCheckNames = ExtractCheckNamesFromLog(log),
            LogExcerpt = log,
        };

        // ── Check built-in infrastructure rules first ────────────────
        var matchedBuiltIn = await Task.Run(() => Array.Find(_builtInRules, r => IsRegexMatch(r.Pattern, log)));
        if (matchedBuiltIn != default)
        {
            _logger.Info($"FlakinessService: built-in rule '{matchedBuiltIn.Description}' matched {prKey}. Rerunning.");
            await RerunAndNotify(pr, owner, repo, primaryRunId, prKey, maxAttempts, $"Matched built-in rule: {matchedBuiltIn.Description}");
            return;
        }

        // ── Check local rules first ──────────────────────────────────
        // Snapshot on this (UI) thread; only the matching runs on the thread pool.
        var enabledRules = _settings.FlakinessRules.Where(r => r.IsEnabled).ToList();
        var matchedRule = await Task.Run(() => enabledRules.FirstOrDefault(r => IsRegexMatch(r.Pattern, log)));

        if (matchedRule is not null)
        {
            matchedRule.MatchCount++;
            _settings.Save();
            _logger.Info($"FlakinessService: local rule '{matchedRule.Description}' matched {prKey}. Rerunning.");
            await RerunAndNotify(pr, owner, repo, primaryRunId, prKey, maxAttempts, $"Matched rule: {matchedRule.Description}");
            return;
        }

        // ── Call the AI provider ─────────────────────────────────────
        var analyzer = _settings.FlakinessAiProvider == AppSettings.FlakinessAiProviderClaude ? _claude : _gitHubModels;
        _logger.Info($"FlakinessService: no local rule matched {prKey}, calling {analyzer.Name} for analysis.");
        var result = await analyzer.AnalyzeFlakiness(context, _settings.FlakinessCustomHints);

        // If the full log triggered the GitHub Models content filter, retry once with error-lines-only excerpt.
        // Claude has no such filter, and a failed claude run would only fail again more slowly.
        if (result.IsIndeterminate && analyzer == _gitHubModels)
        {
            var errorLinesExcerpt = GitHubService.ExtractErrorLines(log);
            if (errorLinesExcerpt != log) // ExtractErrorLines returns the original when too few lines found
            {
                _logger.Info($"FlakinessService: retrying {analyzer.Name} for {prKey} with error-lines-only excerpt.");
                var retryContext = new FailureContext
                {
                    Repository = context.Repository,
                    PrNumber = context.PrNumber,
                    PrTitle = context.PrTitle,
                    HeadBranch = context.HeadBranch,
                    HeadCommitSha = context.HeadCommitSha,
                    FailedCheckNames = context.FailedCheckNames,
                    LogExcerpt = errorLinesExcerpt,
                };
                result = await analyzer.AnalyzeFlakiness(retryContext, _settings.FlakinessCustomHints);
            }
        }

        // Persist any suggested rules (deduplicate by pattern; validate before saving)
        foreach (var suggestion in result.SuggestedRules)
        {
            if (!IsValidFlakinessPattern(suggestion.Pattern))
            {
                _logger.Warn($"FlakinessService: rejected AI-suggested pattern (failed validation): '{suggestion.Pattern}'");
                continue;
            }
            if (_settings.FlakinessRules.All(r => r.Pattern != suggestion.Pattern))
            {
                _settings.FlakinessRules.Add(new FlakinessRule
                {
                    Pattern = suggestion.Pattern,
                    Description = suggestion.Description,
                });
                _logger.Info($"FlakinessService: added new flakiness rule '{suggestion.Description}' (pattern: {suggestion.Pattern}).");
            }
        }

        if (result.SuggestedRules.Count > 0)
            _settings.Save();

        if (result.IsIndeterminate)
        {
            _logger.Info($"FlakinessService: {analyzer.Name} analysis indeterminate for {prKey}: {result.Rationale}. No action taken.");
            return;
        }

        if (result.IsFlaky)
        {
            _logger.Info($"FlakinessService: {analyzer.Name} says FLAKY for {prKey}: {result.Rationale}");
            await RerunAndNotify(pr, owner, repo, primaryRunId, prKey, maxAttempts, result.Rationale);
        }
        else
        {
            _logger.Info($"FlakinessService: {analyzer.Name} says REAL FAILURE for {prKey}: {result.Rationale}");
            RealFailureClassified?.Invoke(prKey);
            if (_settings.NotifyFlakinessRealFailure)
                _notifications.Notify(
                    $"\u274c Real failure on #{pr.Number} ({pr.Repository})",
                    result.Rationale);
        }
    }

    private async Task RerunAndNotify(PullRequestInfo pr, string owner, string repo, long runId, string prKey, int maxAttempts, string rationale)
    {
        var success = await _github.RerunFailedJobsAsync(owner, repo, runId);
        if (!success)
        {
            _logger.Warn($"FlakinessService: rerun failed for run {runId} on {prKey}.");
            return;
        }

        var newCount = IncrementRerunCount(prKey);
        _settings.Save();

        FlakyRerunTriggered?.Invoke(prKey);
        _logger.Info($"FlakinessService: triggered rerun {newCount}/{maxAttempts} for {prKey}.");
        if (_settings.NotifyFlakinessRerun)
            _notifications.Notify(
                $"\ud83d\udd04 Flaky CI on #{pr.Number} \u2014 retrying ({newCount}/{maxAttempts})",
                rationale);
    }

    private int GetRerunCount(string prKey)
    {
        return _settings.FlakinessRerunCounts.TryGetValue(prKey, out var record) ? record.Count : 0;
    }

    private int IncrementRerunCount(string prKey)
    {
        if (!_settings.FlakinessRerunCounts.TryGetValue(prKey, out var record))
        {
            record = new RerunRecord();
            _settings.FlakinessRerunCounts[prKey] = record;
        }
        record.Count++;
        record.LastAttempt = DateTimeOffset.UtcNow;
        return record.Count;
    }

    internal static bool IsRegexMatch(string pattern, string input)
    {
        if (string.IsNullOrWhiteSpace(pattern) || string.IsNullOrWhiteSpace(input))
            return false;
        try
        {
            return Regex.IsMatch(input, pattern, RegexOptions.IgnoreCase | RegexOptions.Multiline, TimeSpan.FromSeconds(1));
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Minimum letters/digits an AI-suggested pattern must contain outside escapes.</summary>
    private const int MinLiteralChars = 10;

    /// <summary>
    /// Lines that appear in nearly every failing CI log. A suggested rule matching one of them
    /// would rerun every failure in every repository without asking the AI again.
    /// </summary>
    private static readonly string[] GenericFailureLines =
    [
        "Error: Process completed with exit code 1.",
        "##[error]Process completed with exit code 1.",
        "npm ERR! code ELIFECYCLE",
        "Build FAILED.",
        "Test run failed. 1 test failed, 10 passed.",
        "FAILED tests/test_example.py::test_example - AssertionError",
    ];

    /// <summary>
    /// Validates an AI-suggested regex pattern before persisting it. Suggested rules are saved
    /// and enabled without review, and their input (the CI log) is controlled by PR authors, so
    /// overly broad, invalid, or generic patterns are rejected.
    /// </summary>
    internal static bool IsValidFlakinessPattern(string? pattern)
    {
        if (string.IsNullOrWhiteSpace(pattern) || CountLiteralChars(pattern) < MinLiteralChars)
            return false;
        try
        {
            var timeout = TimeSpan.FromMilliseconds(100);

            // Reject patterns that match an empty string (catches .*, ^, etc.)
            if (Regex.IsMatch("", pattern, RegexOptions.None, timeout))
                return false;

            return !GenericFailureLines.Any(line =>
                Regex.IsMatch(line, pattern, RegexOptions.IgnoreCase | RegexOptions.Multiline, timeout));
        }
        catch
        {
            // Invalid regex
            return false;
        }
    }

    /// <summary>Letters and digits in <paramref name="pattern"/>, not counting escapes (\s, \b) or inline options ((?i)).</summary>
    private static int CountLiteralChars(string pattern)
    {
        var stripped = Regex.Replace(pattern, @"\(\?[a-zA-Z-]+\)|\\.", "");
        return stripped.Count(char.IsLetterOrDigit);
    }

    internal static IReadOnlyList<string> ExtractCheckNamesFromLog(string log)
    {
        if (string.IsNullOrWhiteSpace(log))
            return [];

        // gh run view log lines start with "JOBNAME\tSTEP\t..."
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in log.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = line.Split('\t', 2);
            if (parts.Length >= 1 && !string.IsNullOrWhiteSpace(parts[0]))
                names.Add(parts[0].Trim());
            if (names.Count >= 10) break;
        }
        return names.ToList();
    }
}
