using PrBot.Models;
using PrBot.Settings;

namespace PrBot.Services;

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
    private System.Timers.Timer? _timer;

    private Dictionary<string, PullRequestInfo> _previousAutoMerge = new();
    private Dictionary<string, PullRequestInfo> _previousReviews = new();

    public PollingService(GitHubService github, AppSettings settings)
    {
        _github = github;
        _settings = settings;
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
        try
        {
            // Fetch all my PRs in a single API call, then split by auto-merge flag
            var allMyPrs  = await _github.FetchAllMyPRsAsync(_settings.Organizations);
            var autoMergePrs = allMyPrs.Where(p => p.HasAutoMerge).ToList();
            var myPrs        = allMyPrs.Where(p => !p.HasAutoMerge).ToList();

            var reviewPrs = await _github.FetchPRsAwaitingMyReviewAsync(_settings.Organizations);
            var hotfixPrs = await _github.FetchHotfixPRsAsync(_settings.Organizations);

            DetectAutoMergeChanges(autoMergePrs);
            DetectReviewChanges(reviewPrs);

            var snapshot = new PollSnapshot
            {
                AutoMergePrs = autoMergePrs,
                MyPrs        = myPrs,
                ReviewRequestedPrs = reviewPrs,
                HotfixPrs = hotfixPrs,
            };

            LatestSnapshot = snapshot;
            Polled?.Invoke(this, snapshot);
        }
        catch
        {
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
