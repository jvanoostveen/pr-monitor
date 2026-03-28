using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using PrMonitor.Models;
using PrMonitor.Services;
using PrMonitor.Settings;

namespace PrMonitor.ViewModels;

/// <summary>
/// ViewModel for the floating PR monitor window.
/// Binds to poll data and exposes observable collections.
/// </summary>
public sealed class MainViewModel : INotifyPropertyChanged
{
    private readonly AppSettings _settings;
    private readonly NotificationService _notificationService;
    private PollingService? _polling;
    private bool _startupSummaryShown;

    public MainViewModel(AppSettings settings, NotificationService notificationService)
    {
        _settings = settings;
        _notificationService = notificationService;
        _hiddenCount = settings.HiddenPrKeys.Count;
    }

    public ObservableCollection<PrItemViewModel> AutoMergePrs { get; } = [];
    public ObservableCollection<PrItemViewModel> MyPrs { get; } = [];
    public ObservableCollection<PrItemViewModel> ReviewRequestedPrs { get; } = [];
    public ObservableCollection<PrItemViewModel> TeamReviewRequestedPrs { get; } = [];
    public ObservableCollection<PrItemViewModel> HotfixPrs { get; } = [];
    public ObservableCollection<PrItemViewModel> HiddenPrs { get; } = [];

    private int _autoMergeCount;
    public int AutoMergeCount
    {
        get => _autoMergeCount;
        private set => SetField(ref _autoMergeCount, value);
    }

    private int _reviewCount;
    public int ReviewCount
    {
        get => _reviewCount;
        private set => SetField(ref _reviewCount, value);
    }

    private int _teamReviewCount;
    public int TeamReviewCount
    {
        get => _teamReviewCount;
        private set => SetField(ref _teamReviewCount, value);
    }

    private int _myPrsCount;
    public int MyPrsCount
    {
        get => _myPrsCount;
        private set => SetField(ref _myPrsCount, value);
    }

    private int _hotfixCount;
    public int HotfixCount
    {
        get => _hotfixCount;
        private set => SetField(ref _hotfixCount, value);
    }

    private int _hiddenCount;
    public int HiddenCount
    {
        get => _hiddenCount;
        private set => SetField(ref _hiddenCount, value);
    }

    private string _lastUpdated = "—";
    public string LastUpdated
    {
        get => _lastUpdated;
        private set => SetField(ref _lastUpdated, value);
    }

    private bool _isRefreshing;
    public bool IsRefreshing
    {
        get => _isRefreshing;
        private set => SetField(ref _isRefreshing, value);
    }

    private bool _isOffline;
    public bool IsOffline
    {
        get => _isOffline;
        private set => SetField(ref _isOffline, value);
    }

    private bool _updateAvailable;
    public bool UpdateAvailable
    {
        get => _updateAvailable;
        private set
        {
            if (_updateAvailable == value) return;
            _updateAvailable = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(UpdateNotAvailable));
        }
    }

    public bool UpdateNotAvailable => !UpdateAvailable;

    private string _latestVersion = "";
    public string LatestVersion
    {
        get => _latestVersion;
        private set => SetField(ref _latestVersion, value);
    }

    private string _updateReleaseUrl = "";
    public string UpdateReleaseUrl
    {
        get => _updateReleaseUrl;
        private set => SetField(ref _updateReleaseUrl, value);
    }

    public bool AutoMergeExpanded
    {
        get => _settings.AutoMergeExpanded;
        set
        {
            if (_settings.AutoMergeExpanded == value) return;
            _settings.AutoMergeExpanded = value;
            _settings.Save();
            OnPropertyChanged();
        }
    }

    public bool ReviewExpanded
    {
        get => _settings.ReviewExpanded;
        set
        {
            if (_settings.ReviewExpanded == value) return;
            _settings.ReviewExpanded = value;
            _settings.Save();
            OnPropertyChanged();
        }
    }

    public bool HotfixExpanded
    {
        get => _settings.HotfixExpanded;
        set
        {
            if (_settings.HotfixExpanded == value) return;
            _settings.HotfixExpanded = value;
            _settings.Save();
            OnPropertyChanged();
        }
    }

    public bool MyPrsExpanded
    {
        get => _settings.MyPrsExpanded;
        set
        {
            if (_settings.MyPrsExpanded == value) return;
            _settings.MyPrsExpanded = value;
            _settings.Save();
            OnPropertyChanged();
        }
    }

    public bool LaterExpanded
    {
        get => _settings.LaterExpanded;
        set
        {
            if (_settings.LaterExpanded == value) return;
            _settings.LaterExpanded = value;
            _settings.Save();
            OnPropertyChanged();
        }
    }

    public bool TeamReviewExpanded
    {
        get => _settings.TeamReviewExpanded;
        set
        {
            if (_settings.TeamReviewExpanded == value) return;
            _settings.TeamReviewExpanded = value;
            _settings.Save();
            OnPropertyChanged();
        }
    }

    public bool ShowTeamReviewSection
    {
        get => _settings.ShowTeamReviewSection;
        set
        {
            if (_settings.ShowTeamReviewSection == value) return;
            _settings.ShowTeamReviewSection = value;
            _settings.Save();
            OnPropertyChanged();
        }
    }

    /// <summary>Called after a PR is moved to Later or restored from Later.</summary>
    public Action? OnHiddenPrsChanged { get; set; }

    public void ToggleAutoMergeExpanded() => AutoMergeExpanded = !AutoMergeExpanded;
    public void ToggleReviewExpanded() => ReviewExpanded = !ReviewExpanded;
    public void ToggleHotfixExpanded() => HotfixExpanded = !HotfixExpanded;
    public void ToggleMyPrsExpanded() => MyPrsExpanded = !MyPrsExpanded;
    public void ToggleLaterExpanded() => LaterExpanded = !LaterExpanded;
    public void ToggleTeamReviewExpanded() => TeamReviewExpanded = !TeamReviewExpanded;

    private static readonly TimeSpan StaleCooldown = TimeSpan.FromDays(14);

    public void HideItem(string key, DateTimeOffset? until = null)
    {
        _settings.HiddenPrKeys.Add(key);
        _settings.HiddenPrLastSeen[key] = DateTimeOffset.UtcNow;
        _settings.SnoozedPrs[key] = until ?? DateTimeOffset.MaxValue;
        _settings.Save();

        // Find item in active lists, move it to HiddenPrs immediately
        var item = FindAndRemove(HotfixPrs, key)
                ?? FindAndRemove(AutoMergePrs, key)
                ?? FindAndRemove(MyPrs, key)
                ?? FindAndRemove(ReviewRequestedPrs, key)
                ?? FindAndRemove(TeamReviewRequestedPrs, key);
        if (item is not null)
        {
            var wasEmpty = HiddenPrs.Count == 0;
            HiddenPrs.Add(item);
            // Only auto-expand Later if it was empty before — don't override user's collapsed state
            if (wasEmpty)
                LaterExpanded = true;
        }

        AutoMergeCount = AutoMergePrs.Count;
        MyPrsCount = MyPrs.Count;
        ReviewCount = ReviewRequestedPrs.Count;
        TeamReviewCount = TeamReviewRequestedPrs.Count;
        HotfixCount = HotfixPrs.Count;
        HiddenCount = HiddenPrs.Count;
        OnHiddenPrsChanged?.Invoke();
    }

    public void RestoreItem(string key)
    {
        _settings.HiddenPrKeys.Remove(key);
        _settings.HiddenPrLastSeen.Remove(key);
        _settings.SnoozedPrs.Remove(key);
        _settings.Save();
        var item = FindAndRemove(HiddenPrs, key);
        if (item is not null)
        {
            // Put back into the correct section immediately
            if (item.IsHotfixPr)
            {
                HotfixPrs.Add(item);
                HotfixCount = HotfixPrs.Count;
            }
            else if (item.IsAutoMergePr)
            {
                AutoMergePrs.Add(item);
                AutoMergeCount = AutoMergePrs.Count;
            }
            else if (item.IsMyPr)
            {
                MyPrs.Add(item);
                MyPrsCount = MyPrs.Count;
            }
            else if (item.IsTeamReviewPr)
            {
                TeamReviewRequestedPrs.Add(item);
                TeamReviewCount = TeamReviewRequestedPrs.Count;
            }
            else
            {
                ReviewRequestedPrs.Add(item);
                ReviewCount = ReviewRequestedPrs.Count;
            }
        }
        HiddenCount = HiddenPrs.Count;
        OnHiddenPrsChanged?.Invoke();
    }

    private static PrItemViewModel? FindAndRemove(ObservableCollection<PrItemViewModel> list, string key)
    {
        var item = list.FirstOrDefault(p => p.Key == key);
        if (item is not null) list.Remove(item);
        return item;
    }

    // ── Subscribe ───────────────────────────────────────────────────

    public void Subscribe(PollingService polling)
    {
        _polling = polling;
        polling.Polled += (_, snapshot) =>
        {
            System.Windows.Application.Current?.Dispatcher.Invoke(() =>
            {
                IsOffline = false;
                IsRefreshing = false;
                UpdateFromSnapshot(snapshot);
                if (!_startupSummaryShown)
                {
                    _startupSummaryShown = true;
                    if (_settings.NotifyStartupSummary)
                        ShowStartupSummary();
                }
            });
        };
        polling.PollFailed += ex =>
            System.Windows.Application.Current?.Dispatcher.Invoke(() => IsOffline = true);
    }

    // ── Commands ────────────────────────────────────────────────────

    public async Task RefreshAsync()
    {
        if (_polling is null || IsRefreshing) return;
        IsRefreshing = true;
        await _polling.RefreshAsync();
    }

    public void OpenMyPrsInBrowser() =>
        OpenUrl("https://github.com/pulls?q=is%3Aopen+is%3Apr+author%3A%40me");

    public void OpenReviewsInBrowser() =>
        OpenUrl("https://github.com/pulls?q=is%3Aopen+is%3Apr+review-requested%3A%40me");

    public void SetUpdateAvailable(string version, string url)
    {
        LatestVersion = version;
        UpdateReleaseUrl = url;
        UpdateAvailable = true;
    }

    public void OpenUpdateRelease()
    {
        if (!string.IsNullOrWhiteSpace(UpdateReleaseUrl)
            && Uri.TryCreate(UpdateReleaseUrl, UriKind.Absolute, out var uri)
            && uri.Scheme == Uri.UriSchemeHttps)
            Process.Start(new ProcessStartInfo(UpdateReleaseUrl) { UseShellExecute = true });
    }

    // ── Internals ───────────────────────────────────────────────────

    private void UpdateFromSnapshot(PollSnapshot snapshot)
    {
        // All PR keys seen in this poll
        var allKeys = snapshot.AutoMergePrs.Select(p => p.Key)
            .Concat(snapshot.MyPrs.Select(p => p.Key))
            .Concat(snapshot.ReviewRequestedPrs.Select(p => p.Key))
            .Concat(snapshot.TeamReviewRequestedPrs.Select(p => p.Key))
            .Concat(snapshot.HotfixPrs.Select(p => p.Key))
            .ToHashSet();

        // Guard: if the poll returned nothing at all (network failure / auth error),
        // skip stale-key cleanup entirely to avoid wrongly evicting hidden PRs.
        if (allKeys.Count > 0 || _settings.HiddenPrKeys.Count == 0)
        {
            var now = DateTimeOffset.UtcNow;

            // Initialize last-seen for any key that has no record yet (e.g. migrated settings)
            bool lastSeenChanged = false;
            foreach (var key in _settings.HiddenPrKeys.Where(k => !_settings.HiddenPrLastSeen.ContainsKey(k)))
            {
                _settings.HiddenPrLastSeen[key] = now;
                lastSeenChanged = true;
            }

            // Update last-seen timestamp for every hidden key present in this poll
            foreach (var key in _settings.HiddenPrKeys.Where(k => allKeys.Contains(k)))
            {
                _settings.HiddenPrLastSeen[key] = now;
                lastSeenChanged = true;
            }

            // Remove keys that have been gone for longer than the cooldown period
            var stale = _settings.HiddenPrKeys
                .Where(k => !allKeys.Contains(k) &&
                            _settings.HiddenPrLastSeen.TryGetValue(k, out var seen) &&
                            (now - seen) > StaleCooldown)
                .ToList();
            foreach (var k in stale)
            {
                _settings.HiddenPrKeys.Remove(k);
                _settings.HiddenPrLastSeen.Remove(k);
            }

            if (lastSeenChanged || stale.Count > 0) _settings.Save();
        }

        // Auto-restore snoozed PRs whose timer has expired
        var wakingKeys = _settings.SnoozedPrs
            .Where(kv => kv.Value != DateTimeOffset.MaxValue && kv.Value <= DateTimeOffset.UtcNow)
            .Select(kv => kv.Key)
            .ToList();
        foreach (var k in wakingKeys)
            RestoreItem(k);

        var hidden = _settings.HiddenPrKeys;

        AutoMergePrs.Clear();
        foreach (var pr in snapshot.AutoMergePrs)
        {
            if (!hidden.Contains(pr.Key))
                AutoMergePrs.Add(PrItemViewModel.From(pr, isAutoMerge: true));
        }

        MyPrs.Clear();
        foreach (var pr in snapshot.MyPrs)
        {
            if (!hidden.Contains(pr.Key))
                MyPrs.Add(PrItemViewModel.From(pr, isMyPr: true));
        }

        ReviewRequestedPrs.Clear();
        foreach (var pr in snapshot.ReviewRequestedPrs)
        {
            if (!hidden.Contains(pr.Key))
                ReviewRequestedPrs.Add(PrItemViewModel.From(pr, isAutoMerge: false));
        }

        TeamReviewRequestedPrs.Clear();
        foreach (var pr in snapshot.TeamReviewRequestedPrs)
        {
            if (!hidden.Contains(pr.Key))
                TeamReviewRequestedPrs.Add(PrItemViewModel.From(pr, isTeamReview: true));
        }

        HotfixPrs.Clear();
        foreach (var pr in snapshot.HotfixPrs)
        {
            if (!hidden.Contains(pr.Key))
                HotfixPrs.Add(PrItemViewModel.From(pr, isHotfix: true));
        }

        // Rebuild hidden list from all PRs in this snapshot
        HiddenPrs.Clear();
        foreach (var x in snapshot.AutoMergePrs.Select(p => (pr: p, isAm: true, isMyPr: false, isHotfix: false, isTeamReview: false))
                     .Concat(snapshot.MyPrs.Select(p => (pr: p, isAm: false, isMyPr: true, isHotfix: false, isTeamReview: false)))
                     .Concat(snapshot.ReviewRequestedPrs.Select(p => (pr: p, isAm: false, isMyPr: false, isHotfix: false, isTeamReview: false)))
                     .Concat(snapshot.TeamReviewRequestedPrs.Select(p => (pr: p, isAm: false, isMyPr: false, isHotfix: false, isTeamReview: true)))
                     .Concat(snapshot.HotfixPrs.Select(p => (pr: p, isAm: false, isMyPr: false, isHotfix: true, isTeamReview: false)))
                     .DistinctBy(x => x.pr.Key)
                     .Where(x => hidden.Contains(x.pr.Key)))
        {
            HiddenPrs.Add(PrItemViewModel.From(x.pr, isAutoMerge: x.isAm, isMyPr: x.isMyPr, isHotfix: x.isHotfix, isTeamReview: x.isTeamReview,
                snoozedUntilText: FormatSnoozedUntil(_settings.SnoozedPrs.GetValueOrDefault(x.pr.Key, DateTimeOffset.MaxValue))));
        }

        AutoMergeCount = AutoMergePrs.Count;
        MyPrsCount = MyPrs.Count;
        ReviewCount = ReviewRequestedPrs.Count;
        TeamReviewCount = TeamReviewRequestedPrs.Count;
        HotfixCount = HotfixPrs.Count;
        HiddenCount = HiddenPrs.Count;
        LastUpdated = DateTime.Now.ToString("HH:mm:ss");
    }

    private static void OpenUrl(string url)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps)
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
    }

    private void ShowStartupSummary()
    {
        var parts = new List<string>();
        if (HotfixCount > 0)     parts.Add($"{HotfixCount} hotfix{(HotfixCount > 1 ? "es" : "")}");
        if (AutoMergeCount > 0)  parts.Add($"{AutoMergeCount} auto-merge PR{(AutoMergeCount > 1 ? "s" : "")}");
        if (ReviewCount > 0)     parts.Add($"{ReviewCount} review request{(ReviewCount > 1 ? "s" : "")}");
        if (MyPrsCount > 0)      parts.Add($"{MyPrsCount} own PR{(MyPrsCount > 1 ? "s" : "")}");
        if (TeamReviewCount > 0) parts.Add($"{TeamReviewCount} team review{(TeamReviewCount > 1 ? "s" : "")}");
        if (parts.Count == 0) return;
        _notificationService.Notify("PR Monitor", string.Join(" · ", parts));
    }

    private static string FormatSnoozedUntil(DateTimeOffset until)
    {
        if (until == DateTimeOffset.MaxValue) return "Indefinitely";
        var local = until.ToLocalTime();
        var now = DateTimeOffset.Now;
        var diff = local - now;
        if (diff.TotalMinutes < 90) return $"Until {(int)diff.TotalMinutes + 1}m";
        if (diff.TotalHours < 24) return $"Until {local:HH:mm}";
        if (diff.TotalDays < 7) return $"Until {local:ddd HH:mm}";
        return $"Until {local:MMM d}";
    }

    // ── INotifyPropertyChanged ──────────────────────────────────────

    public event PropertyChangedEventHandler? PropertyChanged;

    private void SetField<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>
/// View model for a single PR row in the list.
/// </summary>
public sealed class PrItemViewModel
{
    public required string Key { get; init; }
    public required string Repository { get; init; }
    public required string Title { get; init; }
    public required string Url { get; init; }
    public required string Author { get; init; }
    public required string TimeAgo { get; init; }
    public string SnoozedUntilText { get; init; } = "";
    public string CreatedAtFormatted { get; init; } = "";
    public required string CIIcon { get; init; }
    public required CIState CIState { get; init; }
    public int UnresolvedReviewCommentCount { get; init; }
    public bool HasUnresolvedReviewComments => UnresolvedReviewCommentCount > 0;
    public string UnresolvedReviewCommentsToolTip => UnresolvedReviewCommentCount == 1
        ? "1 unresolved review comment"
        : $"{UnresolvedReviewCommentCount} unresolved review comments";
    public int Number { get; init; }
    public bool IsAutoMergePr { get; init; }
    public bool IsMyPr { get; init; }
    public bool IsHotfixPr { get; init; }
    public bool IsTeamReviewPr { get; init; }
    public bool IsDraft { get; init; }
    public string HeadRefName { get; init; } = "";
    public string HeadCommitSha { get; init; } = "";
    public bool IsApproved { get; init; }
    public IReadOnlyList<string> ReviewerLogins { get; init; } = [];
    public bool HasNonCopilotReviewer => ReviewerLogins.Count > 0;
    public bool IsOwnPr => IsMyPr || IsAutoMergePr || IsHotfixPr;
    public bool ShowNoReviewerWarning => IsOwnPr && !HasNonCopilotReviewer;
    public string ReviewerTooltip => HasNonCopilotReviewer
        ? string.Join(", ", ReviewerLogins)
        : "No reviewer assigned";
    public string PrTooltip
    {
        get
        {
            var parts = new System.Collections.Generic.List<string>();
            parts.Add($"Opened: {CreatedAtFormatted}");
            parts.Add($"CI: {CIState}");
            if (IsOwnPr)
                parts.Add(HasNonCopilotReviewer
                    ? $"Reviewers: {string.Join(", ", ReviewerLogins)}"
                    : "No reviewer assigned");
            if (HasUnresolvedReviewComments)
                parts.Add(UnresolvedReviewCommentsToolTip);
            if (ShowApprovedIcon)
                parts.Add("Approved");
            return string.Join(System.Environment.NewLine, parts);
        }
    }
    public bool CanRerunFailedJobs => !IsDraft && CIState == CIState.Failure && !string.IsNullOrWhiteSpace(HeadCommitSha);
    public bool CanRequestCopilotReview => !IsDraft;
    public bool CanMarkAsReady => IsOwnPr && IsDraft;
    public bool CanConvertToDraft => IsOwnPr && !IsDraft;

    /// <summary>Show the approved checkmark icon: PR is approved but has no unresolved review comments (comments take priority).</summary>
    public bool ShowApprovedIcon => IsApproved && !HasUnresolvedReviewComments;

    /// <summary>
    /// CI state used for the indicator: always Unknown (grey) for draft PRs.
    /// </summary>
    public CIState EffectiveCIState => IsDraft ? CIState.Unknown : CIState;

    public void OpenInBrowser()
    {
        if (Uri.TryCreate(Url, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps)
            Process.Start(new ProcessStartInfo(Url) { UseShellExecute = true });
    }

    public static PrItemViewModel From(PullRequestInfo pr, bool isAutoMerge = false, bool isMyPr = false, bool isHotfix = false, bool isTeamReview = false, string snoozedUntilText = "") => new()
    {
        Key = pr.Key,
        Repository = pr.Repository,
        Title = pr.Title,
        Url = pr.Url,
        Author = pr.Author,
        Number = pr.Number,
        CIState = pr.CIState,
        UnresolvedReviewCommentCount = pr.UnresolvedReviewCommentCount,
        IsAutoMergePr = isAutoMerge,
        IsMyPr = isMyPr,
        IsHotfixPr = isHotfix,
        IsTeamReviewPr = isTeamReview,
        IsDraft = pr.IsDraft,
        HeadRefName = pr.HeadRefName,
        HeadCommitSha = pr.HeadCommitSha,
        IsApproved = pr.IsApproved,
        ReviewerLogins = pr.ReviewerLogins,
        CIIcon = pr.CIState switch
        {
            CIState.Success => "✅",
            CIState.Failure => "❌",
            CIState.Pending => "⏳",
            CIState.Error => "⚠️",
            _ => "❔",
        },
        TimeAgo = FormatTimeAgo(pr.UpdatedAt != DateTimeOffset.MinValue ? pr.UpdatedAt : pr.CreatedAt),
        CreatedAtFormatted = pr.CreatedAt.ToLocalTime().ToString("MMM d, yyyy"),
        SnoozedUntilText = snoozedUntilText,
    };

    internal static string FormatTimeAgo(DateTimeOffset created)
    {
        var span = DateTimeOffset.Now - created;
        if (span.TotalMinutes < 1) return "just now";
        if (span.TotalHours < 1) return $"{(int)span.TotalMinutes}m ago";
        if (span.TotalDays < 1) return $"{(int)span.TotalHours}h ago";
        if (span.TotalDays < 30) return $"{(int)span.TotalDays}d ago";
        return created.ToString("MMM dd");
    }
}
