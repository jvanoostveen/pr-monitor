using PrMonitor.Models;
using PrMonitor.Settings;

namespace PrMonitor.Services;

/// <summary>
/// Event args carrying information about a change detected between polls.
/// </summary>
public sealed class PrChangeEventArgs : EventArgs
{
    public required PullRequestInfo PullRequest { get; init; }
    public required PrChangeKind Kind { get; init; }
    /// <summary>Only set for <see cref="PrChangeKind.CIStatusChanged"/>.</summary>
    public CIState PreviousCIState { get; init; }
}

public enum PrChangeKind
{
    NewAutoMergePr,
    RemovedAutoMergePr,
    CIStatusChanged,
    NewReviewRequested,
    ReviewRequestRemoved,
}

/// <summary>
/// Snapshot of the latest poll results plus aggregate counts.
/// </summary>
public sealed class PollSnapshot
{
    public IReadOnlyList<PullRequestInfo> AutoMergePrs { get; init; } = [];
    public IReadOnlyList<PullRequestInfo> MyPrs { get; init; } = [];
    public IReadOnlyList<PullRequestInfo> ReviewRequestedPrs { get; init; } = [];
    public IReadOnlyList<PullRequestInfo> TeamReviewRequestedPrs { get; init; } = [];
    public IReadOnlyList<PullRequestInfo> HotfixPrs { get; init; } = [];
    public int FailedCICount => AutoMergePrs.Count(p => p.CIState == CIState.Failure);
    public int PendingCICount => AutoMergePrs.Count(p => p.CIState is CIState.Pending or CIState.Unknown);
    public int TotalCount => AutoMergePrs.Count + ReviewRequestedPrs.Count;
}

/// <summary>
/// Periodically polls GitHub and emits events when PR state changes.
/// </summary>
public sealed class PollingService : IDisposable
{
    private readonly GitHubService _github;
    private readonly AppSettings _settings;
    private readonly DiagnosticsLogger _logger;
    private System.Timers.Timer? _timer;

    private Dictionary<string, PullRequestInfo> _previousAutoMerge = new();
    private Dictionary<string, PullRequestInfo> _previousReviews = new();

    public PollingService(GitHubService github, AppSettings settings, DiagnosticsLogger logger)
    {
        _github = github;
        _settings = settings;
        _logger = logger;
    }

    // ── Events ──────────────────────────────────────────────────────────

    /// <summary>Raised for every individual change detected.</summary>
    public event EventHandler<PrChangeEventArgs>? PrChanged;

    /// <summary>Raised after every completed poll with the full snapshot.</summary>
    public event EventHandler<PollSnapshot>? Polled;

    /// <summary>The most recent snapshot (null before first poll).</summary>
    public PollSnapshot? LatestSnapshot { get; private set; }

    // ── Lifecycle ───────────────────────────────────────────────────────

    public void Start()
    {
        // Fire immediately, then on interval
        _ = PollAsync();

        _timer = new System.Timers.Timer(_settings.PollingIntervalSeconds * 1000);
        _timer.Elapsed += async (_, _) => await PollAsync();
        _timer.AutoReset = true;
        _timer.Start();
    }

    public void UpdateInterval(int seconds)
    {
        if (_timer is not null)
            _timer.Interval = seconds * 1000;
    }

    /// <summary>
    /// Trigger an immediate poll outside the regular interval.
    /// </summary>
    public Task RefreshAsync() => PollAsync();

    public void Dispose()
    {
        _timer?.Stop();
        _timer?.Dispose();
    }

    // ── Core polling logic ──────────────────────────────────────────────

    private async Task PollAsync()
    {
        _logger.Info("PollingService poll started.");
        try
        {
            // Fetch all my PRs in a single API call, then split by auto-merge flag
            var allMyPrs  = await _github.FetchAllMyPRsAsync(_settings.Organizations);
            var autoMergePrs = allMyPrs.Where(p => p.HasAutoMerge).ToList();

            bool showTeamSection = _settings.ShowTeamReviewSection;
            // Always classify team PRs (classifyTeams always true) so they can be
            // excluded from Awaiting My Review regardless of whether the section is shown.
            var reviewPrs   = await _github.FetchPRsAwaitingMyReviewAsync(_settings.Organizations, classifyTeams: true, currentUsername: _settings.GitHubUsername);
            var assignedPrs = await _github.FetchMyAssignedPRsAsync(_settings.Organizations);
            var hotfixPrs   = await _github.FetchHotfixPRsAsync(_settings.Organizations);

            // Exclude hotfix PRs (release/* targets) from My PRs to avoid duplication
            var hotfixKeys = hotfixPrs.Select(p => p.Key).ToHashSet();
            var myPrs      = allMyPrs.Where(p => !p.HasAutoMerge && !hotfixKeys.Contains(p.Key)).ToList();

            var myPrKeys = allMyPrs.Select(p => p.Key).ToHashSet();

            // Split review PRs into direct-user requests and team-only requests
            var directReviewPrs = reviewPrs.Where(p => !p.IsTeamReviewRequested).ToList();
            var teamOnlyPrs     = reviewPrs.Where(p => p.IsTeamReviewRequested).ToList();

            // Direct review list + assignee-only PRs (not authored by current user)
            var combinedReviewPrs = directReviewPrs
                .Concat(assignedPrs.Where(p => !myPrKeys.Contains(p.Key)))
                .DistinctBy(p => p.Key)
                .ToList();

            // Team section: only when enabled; otherwise drop team PRs entirely (not shown anywhere)
            List<PullRequestInfo> teamReviewPrs;
            if (showTeamSection)
            {
                teamReviewPrs = teamOnlyPrs;
            }
            else
            {
                // Team PRs are hidden completely when the section is disabled
                teamReviewPrs = [];
            }

            DetectAutoMergeChanges(autoMergePrs);
            DetectReviewChanges(combinedReviewPrs);

            var snapshot = new PollSnapshot
            {
                AutoMergePrs           = autoMergePrs,
                MyPrs                  = myPrs,
                ReviewRequestedPrs     = combinedReviewPrs,
                TeamReviewRequestedPrs = teamReviewPrs,
                HotfixPrs              = hotfixPrs,
            };

            LatestSnapshot = snapshot;
            Polled?.Invoke(this, snapshot);

            _logger.Info($"PollingService poll finished. AutoMerge={autoMergePrs.Count}, MyPrs={myPrs.Count}, AwaitingReview={combinedReviewPrs.Count}, TeamReview={teamReviewPrs.Count} (reviewRequested={reviewPrs.Count}, assigned={assignedPrs.Count(p => !myPrKeys.Contains(p.Key))}), Hotfixes={hotfixPrs.Count}.");
        }
        catch (Exception ex)
        {
            _logger.Error("PollingService poll failed.", ex);
            // Swallow – we'll try again next interval.
        }
    }

    private void DetectAutoMergeChanges(List<PullRequestInfo> current)
    {
        var currentDict = current.ToDictionary(p => p.Key);

        // New or changed PRs
        foreach (var pr in current)
        {
            if (_previousAutoMerge.TryGetValue(pr.Key, out var prev))
            {
                if (prev.CIState != pr.CIState)
                {
                    RaiseChange(pr, PrChangeKind.CIStatusChanged, prev.CIState);
                }
            }
            else
            {
                RaiseChange(pr, PrChangeKind.NewAutoMergePr);
            }
        }

        // Removed PRs (merged or auto-merge disabled)
        foreach (var key in _previousAutoMerge.Keys)
        {
            if (!currentDict.ContainsKey(key))
            {
                RaiseChange(_previousAutoMerge[key], PrChangeKind.RemovedAutoMergePr);
            }
        }

        _previousAutoMerge = currentDict;
    }

    private void DetectReviewChanges(List<PullRequestInfo> current)
    {
        var currentDict = current.ToDictionary(p => p.Key);

        foreach (var pr in current)
        {
            if (!_previousReviews.ContainsKey(pr.Key))
            {
                RaiseChange(pr, PrChangeKind.NewReviewRequested);
            }
        }

        foreach (var key in _previousReviews.Keys)
        {
            if (!currentDict.ContainsKey(key))
            {
                RaiseChange(_previousReviews[key], PrChangeKind.ReviewRequestRemoved);
            }
        }

        _previousReviews = currentDict;
    }

    private void RaiseChange(PullRequestInfo pr, PrChangeKind kind, CIState previousCI = CIState.Unknown)
    {
        PrChanged?.Invoke(this, new PrChangeEventArgs
        {
            PullRequest = pr,
            Kind = kind,
            PreviousCIState = previousCI,
        });
    }
}
