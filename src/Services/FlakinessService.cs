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
    private readonly GitHubService _github;
    private readonly CopilotService _copilot;
    private readonly AppSettings _settings;
    private readonly NotificationService _notifications;
    private readonly DiagnosticsLogger _logger;

    public FlakinessService(
        GitHubService github,
        CopilotService copilot,
        AppSettings settings,
        NotificationService notifications,
        DiagnosticsLogger logger)
    {
        _github = github;
        _copilot = copilot;
        _settings = settings;
        _notifications = notifications;
        _logger = logger;
    }

    public void Subscribe(PollingService polling)
    {
        polling.PrChanged += (_, e) =>
        {
            if (e.Kind == PrChangeKind.CIStatusChanged
                && e.PullRequest.CIState == CIState.Failure
                && !e.PullRequest.IsDraft
                && string.Equals(e.PullRequest.Author, _settings.GitHubUsername, StringComparison.OrdinalIgnoreCase))
            {
                _ = HandleCIFailureAsync(e.PullRequest);
            }
        };
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

        // ── Check local rules first ──────────────────────────────────
        var matchedRule = _settings.FlakinessRules
            .Where(r => r.IsEnabled)
            .FirstOrDefault(r => IsRegexMatch(r.Pattern, log));

        if (matchedRule is not null)
        {
            matchedRule.MatchCount++;
            _settings.Save();
            _logger.Info($"FlakinessService: local rule '{matchedRule.Description}' matched {prKey}. Rerunning.");
            await RerunAndNotify(pr, owner, repo, primaryRunId, prKey, maxAttempts, $"Matched rule: {matchedRule.Description}");
            return;
        }

        // ── Call Copilot ─────────────────────────────────────────────
        _logger.Info($"FlakinessService: no local rule matched {prKey}, calling Copilot for analysis.");
        var result = await _copilot.AnalyzeFlakiness(context);

        // Persist any suggested rules (deduplicate by pattern)
        foreach (var suggestion in result.SuggestedRules)
        {
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

        if (result.IsFlaky)
        {
            _logger.Info($"FlakinessService: Copilot says FLAKY for {prKey}: {result.Rationale}");
            await RerunAndNotify(pr, owner, repo, primaryRunId, prKey, maxAttempts, result.Rationale);
        }
        else
        {
            _logger.Info($"FlakinessService: Copilot says REAL FAILURE for {prKey}: {result.Rationale}");
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

        _logger.Info($"FlakinessService: triggered rerun {newCount}/{maxAttempts} for {prKey}.");
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

    private static bool IsRegexMatch(string pattern, string input)
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

    private static IReadOnlyList<string> ExtractCheckNamesFromLog(string log)
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
