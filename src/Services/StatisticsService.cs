using PrMonitor.Models;
using PrMonitor.Settings;

namespace PrMonitor.Services;

/// <summary>
/// Collects activity statistics while the app runs by diffing successive poll
/// snapshots and listening to flakiness-analysis outcomes. Counts are persisted
/// as daily buckets in <see cref="StatisticsStore"/>.
/// </summary>
/// <remarks>
/// Collection is best-effort and only happens while the app is running; there is
/// no historical backfill from GitHub. Two metrics are heuristics:
/// <list type="bullet">
/// <item><c>OwnPrsMerged</c> counts an own PR disappearing from every section
/// (a closed-not-merged PR is also counted here).</item>
/// <item><c>ReviewsCompleted</c> counts a review request disappearing (also fires
/// when the request is withdrawn or the PR is closed by someone else).</item>
/// </list>
/// </remarks>
public sealed class StatisticsService
{
    private readonly StatisticsStore _store;
    private readonly AppSettings _settings;
    private readonly DiagnosticsLogger _logger;
    private readonly object _gate = new();
    private readonly DateTimeOffset _startedAt = DateTimeOffset.UtcNow;

    private bool _baselineEstablished;
    private Dictionary<string, PullRequestInfo> _previousOwnPrs = new();
    private Dictionary<string, PullRequestInfo> _previousReviewPrs = new();

    public StatisticsService(StatisticsStore store, AppSettings settings, DiagnosticsLogger logger)
    {
        _store = store;
        _settings = settings;
        _logger = logger;
    }

    /// <summary>Raised after counters change so an open stats window can refresh.</summary>
    public event Action? StatsChanged;

    /// <summary>The underlying store, exposed for the stats view-model.</summary>
    public StatisticsStore Store => _store;

    public void Subscribe(PollingService polling)
    {
        polling.Polled += (_, snapshot) => ProcessSnapshot(snapshot);
    }

    public void SubscribeFlakiness(FlakinessService flakiness)
    {
        flakiness.FlakyRerunTriggered += _ => Record(StatMetric.FlakyReruns);
        flakiness.RealFailureClassified += _ => Record(StatMetric.RealFailures);
    }

    internal void ProcessSnapshot(PollSnapshot snapshot)
    {
        try
        {
            var username = _settings.GitHubUsername;

            // PRs authored by the current user across all own-PR sections.
            var currentOwn = new Dictionary<string, PullRequestInfo>(StringComparer.Ordinal);
            foreach (var pr in snapshot.AutoMergePrs
                .Concat(snapshot.MyPrs)
                .Concat(snapshot.DraftPrs)
                .Concat(snapshot.HotfixPrs))
            {
                if (!string.IsNullOrEmpty(username)
                    && string.Equals(pr.Author, username, StringComparison.OrdinalIgnoreCase))
                {
                    currentOwn[pr.Key] = pr;
                }
            }

            // PRs awaiting my review.
            var currentReview = new Dictionary<string, PullRequestInfo>(StringComparer.Ordinal);
            foreach (var pr in snapshot.ReviewRequestedPrs.Concat(snapshot.TeamReviewRequestedPrs))
                currentReview[pr.Key] = pr;

            // Keys present anywhere in this snapshot (used to detect a truly merged/closed PR).
            var allCurrentKeys = new HashSet<string>(StringComparer.Ordinal);
            foreach (var pr in snapshot.AutoMergePrs
                .Concat(snapshot.MyPrs)
                .Concat(snapshot.DraftPrs)
                .Concat(snapshot.HotfixPrs)
                .Concat(snapshot.ReviewRequestedPrs)
                .Concat(snapshot.TeamReviewRequestedPrs)
                .Concat(snapshot.DependabotPrs))
            {
                allCurrentKeys.Add(pr.Key);
            }

            lock (_gate)
            {
                // The first snapshot only establishes a baseline; nothing is counted
                // so pre-existing PRs at startup don't inflate the numbers.
                if (!_baselineEstablished)
                {
                    _previousOwnPrs = currentOwn;
                    _previousReviewPrs = currentReview;
                    _baselineEstablished = true;
                    return;
                }

                var changed = false;

                // Own PRs opened: newly seen and created after the app started.
                foreach (var (key, pr) in currentOwn)
                {
                    if (!_previousOwnPrs.ContainsKey(key) && pr.CreatedAt >= _startedAt)
                    {
                        _store.Increment(StatMetric.OwnPrsOpened);
                        changed = true;
                    }
                }

                // Own PRs merged (or closed): gone from every section.
                foreach (var key in _previousOwnPrs.Keys)
                {
                    if (!allCurrentKeys.Contains(key))
                    {
                        _store.Increment(StatMetric.OwnPrsMerged);
                        changed = true;
                    }
                }

                // CI failures on own PRs: transition into Failure.
                foreach (var (key, pr) in currentOwn)
                {
                    if (_previousOwnPrs.TryGetValue(key, out var prev)
                        && prev.CIState != CIState.Failure
                        && pr.CIState == CIState.Failure)
                    {
                        _store.Increment(StatMetric.CiFailures);
                        changed = true;
                    }
                }

                // Reviews completed: a review request that disappeared.
                foreach (var key in _previousReviewPrs.Keys)
                {
                    if (!currentReview.ContainsKey(key))
                    {
                        _store.Increment(StatMetric.ReviewsCompleted);
                        changed = true;
                    }
                }

                _previousOwnPrs = currentOwn;
                _previousReviewPrs = currentReview;

                if (changed)
                {
                    _store.Save();
                }

                if (changed)
                    RaiseStatsChanged();
            }
        }
        catch (Exception ex)
        {
            _logger.Warn($"StatisticsService: failed to process snapshot. {DiagnosticsLogger.SummarizeException(ex)}");
        }
    }

    internal void Record(StatMetric metric)
    {
        try
        {
            lock (_gate)
            {
                _store.Increment(metric);
                _store.Save();
            }
            RaiseStatsChanged();
        }
        catch (Exception ex)
        {
            _logger.Warn($"StatisticsService: failed to record {metric}. {DiagnosticsLogger.SummarizeException(ex)}");
        }
    }

    private void RaiseStatsChanged()
    {
        try
        {
            StatsChanged?.Invoke();
        }
        catch
        {
            // A misbehaving subscriber must not break statistics collection.
        }
    }
}
