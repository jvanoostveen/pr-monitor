using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Windows;
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
    private readonly UpdateService _updateService;
    private PollingService? _polling;
    private bool _startupSummaryShown;
    private string? _tempExePath;

    public MainViewModel(AppSettings settings, NotificationService notificationService, UpdateService updateService)
    {
        _settings = settings;
        _notificationService = notificationService;
        _updateService = updateService;
        _hiddenCount = settings.SnoozedPrs.Keys.Count(settings.HiddenPrKeys.Contains);
    }

    public ObservableCollection<PrItemViewModel> AutoMergePrs { get; } = [];
    public ObservableCollection<PrItemViewModel> MyPrs { get; } = [];
    public ObservableCollection<PrItemViewModel> ReviewRequestedPrs { get; } = [];
    public ObservableCollection<PrItemViewModel> TeamReviewRequestedPrs { get; } = [];
    public ObservableCollection<PrItemViewModel> HotfixPrs { get; } = [];
    public ObservableCollection<PrItemViewModel> DependabotPrs { get; } = [];
    public ObservableCollection<PrItemViewModel> HiddenPrs { get; } = [];
    public ObservableCollection<PrItemViewModel> DraftPrs { get; } = [];

    /// <summary>PRs that belong to a stack, pulled out of their regular section and grouped per stack.</summary>
    public ObservableCollection<PrItemViewModel> StackedPrs { get; } = [];

    /// <summary>Every PR row currently shown in the window, across all sections.</summary>
    internal IEnumerable<PrItemViewModel> AllPrs =>
        HotfixPrs.Concat(AutoMergePrs).Concat(ReviewRequestedPrs).Concat(StackedPrs).Concat(MyPrs)
                 .Concat(DependabotPrs).Concat(TeamReviewRequestedPrs).Concat(DraftPrs).Concat(HiddenPrs);

    /// <summary>Rendered content of the last applied snapshot; see <see cref="BuildDisplaySignature"/>.</summary>
    private string? _lastDisplaySignature;

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

    private int _dependabotCount;
    public int DependabotCount
    {
        get => _dependabotCount;
        private set => SetField(ref _dependabotCount, value);
    }

    private int _draftPrsCount;
    public int DraftPrsCount
    {
        get => _draftPrsCount;
        private set => SetField(ref _draftPrsCount, value);
    }

    private int _stackedCount;
    public int StackedCount
    {
        get => _stackedCount;
        private set => SetField(ref _stackedCount, value);
    }

    private bool _hasLoadedOnce;
    public bool HasLoadedOnce
    {
        get => _hasLoadedOnce;
        private set
        {
            if (_hasLoadedOnce == value) return;
            _hasLoadedOnce = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsInitialLoading));
            OnPropertyChanged(nameof(IsEmptyState));
        }
    }

    /// <summary>True before the first poll has completed — drives the subtle startup loading overlay.</summary>
    public bool IsInitialLoading => !HasLoadedOnce;

    /// <summary>Sum of every section's count, including Later/snoozed (Hidden).</summary>
    public int TotalPrCount =>
 HotfixCount + AutoMergeCount + ReviewCount + TeamReviewCount + MyPrsCount + DependabotCount + DraftPrsCount + StackedCount + HiddenCount;

    /// <summary>True once loaded and there is truly nothing anywhere (incl. Later) — drives the playful empty-state overlay.</summary>
    public bool IsEmptyState => HasLoadedOnce && TotalPrCount == 0;

    private static readonly string[] EmptyStateHeadlines =
    [
        "All clear. Suspiciously quiet out there.",
        "Zero PRs. Look at you go.",
        "Nothing pending. Feels illegal somehow.",
        "Empty queue. Treat yourself.",
        "No PRs in sight. Weird flex, but okay.",
        "Clean slate. Don't jinx it.",
        "Nada. Zilch. Go outside.",
        "Everything's merged. Suspicious, but I'll allow it.",
        "No reviews needed. The bots are proud of you.",
        "Inbox zero. Achievement unlocked.",
    ];

    private readonly Random _emptyStateRandom = new();
    private bool _wasEmpty;

    private string _emptyStateHeadline = EmptyStateHeadlines[0];
    public string EmptyStateHeadline
    {
        get => _emptyStateHeadline;
        private set => SetField(ref _emptyStateHeadline, value);
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
        private set
        {
            if (_latestVersion == value) return;
            _latestVersion = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(UpdateBannerText));
        }
    }

    private string _updateReleaseUrl = "";
    public string UpdateReleaseUrl
    {
        get => _updateReleaseUrl;
        private set => SetField(ref _updateReleaseUrl, value);
    }

    private string _updateReleaseNotesUrl = "";
    public string UpdateReleaseNotesUrl
    {
        get => _updateReleaseNotesUrl;
        private set => SetField(ref _updateReleaseNotesUrl, value);
    }

    private bool _isDownloadingUpdate;
    public bool IsDownloadingUpdate
    {
        get => _isDownloadingUpdate;
        private set
        {
            if (_isDownloadingUpdate == value) return;
            _isDownloadingUpdate = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(UpdateBannerText));
            OnPropertyChanged(nameof(CanClickUpdateBanner));
        }
    }

    private int _downloadProgress;
    public int DownloadProgress
    {
        get => _downloadProgress;
        private set
        {
            if (_downloadProgress == value) return;
            _downloadProgress = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(UpdateBannerText));
        }
    }

    private bool _updateReadyToInstall;
    public bool UpdateReadyToInstall
    {
        get => _updateReadyToInstall;
        private set
        {
            if (_updateReadyToInstall == value) return;
            _updateReadyToInstall = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(UpdateBannerText));
        }
    }

    private bool _updateDownloadFailed;
    public bool UpdateDownloadFailed
    {
        get => _updateDownloadFailed;
        private set
        {
            if (_updateDownloadFailed == value) return;
            _updateDownloadFailed = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(UpdateBannerText));
            OnPropertyChanged(nameof(CanClickUpdateBanner));
        }
    }

    /// <summary>Dynamic text for the update banner depending on current download state.</summary>
    public string UpdateBannerText => (_isDownloadingUpdate, _updateReadyToInstall, _updateDownloadFailed) switch
    {
        (true,  _,    _)    => $"Downloading update… {_downloadProgress}%",
        (_,     true, _)    => $"v{LatestVersion} ready — click to restart",
        (_,     _,    true) => $"Download failed — click to open release page",
        _                   => $"Update available: v{LatestVersion} — click to download",
    };

    /// <summary>False while a download is in progress; prevents double-clicks.</summary>
    public bool CanClickUpdateBanner => !_isDownloadingUpdate;

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

    public bool DependabotExpanded
    {
        get => _settings.DependabotExpanded;
        set
        {
            if (_settings.DependabotExpanded == value) return;
            _settings.DependabotExpanded = value;
            _settings.Save();
            OnPropertyChanged();
        }
    }

    public bool DraftExpanded
    {
        get => _settings.DraftExpanded;
        set
        {
            if (_settings.DraftExpanded == value) return;
            _settings.DraftExpanded = value;
            _settings.Save();
            OnPropertyChanged();
        }
    }

    public bool StacksExpanded
    {
        get => _settings.StacksExpanded;
        set
        {
            if (_settings.StacksExpanded == value) return;
            _settings.StacksExpanded = value;
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
    public void ToggleDependabotExpanded() => DependabotExpanded = !DependabotExpanded;
    public void ToggleDraftExpanded() => DraftExpanded = !DraftExpanded;
    public void ToggleStacksExpanded() => StacksExpanded = !StacksExpanded;
    public void HideCompletely(string key)
    {
        _settings.HiddenPrKeys.Add(key);
        _settings.SnoozedPrs.Remove(key);
        _settings.ManuallyHiddenPrKeys.Add(key);
        _settings.Save();

        // Remove from any visible section without adding it to Later.
        _ = FindAndRemove(HotfixPrs, key)
            ?? FindAndRemove(AutoMergePrs, key)
            ?? FindAndRemove(MyPrs, key)
            ?? FindAndRemove(DraftPrs, key)
            ?? FindAndRemove(ReviewRequestedPrs, key)
            ?? FindAndRemove(TeamReviewRequestedPrs, key)
            ?? FindAndRemove(DependabotPrs, key);

        AutoMergeCount = AutoMergePrs.Count;
        MyPrsCount = MyPrs.Count;
        DraftPrsCount = DraftPrs.Count;
        ReviewCount = ReviewRequestedPrs.Count;
        TeamReviewCount = TeamReviewRequestedPrs.Count;
        HotfixCount = HotfixPrs.Count;
        HiddenCount = HiddenPrs.Count;
        OnHiddenPrsChanged?.Invoke();
    }

    public void HideItem(string key, DateTimeOffset? until = null)
    {
        _settings.HiddenPrKeys.Add(key);
        _settings.SnoozedPrs[key] = until ?? DateTimeOffset.MaxValue;
        _settings.ManuallyHiddenPrKeys.Remove(key);
        _settings.Save();
        _lastDisplaySignature = null;

        // Find item in active lists, move it to HiddenPrs immediately
        var item = FindAndRemove(HotfixPrs, key)
                ?? FindAndRemove(AutoMergePrs, key)
                ?? FindAndRemove(MyPrs, key)
                ?? FindAndRemove(DraftPrs, key)
                ?? FindAndRemove(ReviewRequestedPrs, key)
                ?? FindAndRemove(TeamReviewRequestedPrs, key)
                ?? FindAndRemove(DependabotPrs, key);
        if (item is not null)
        {
            HiddenPrs.Add(item);
        }

        AutoMergeCount = AutoMergePrs.Count;
        MyPrsCount = MyPrs.Count;
        DraftPrsCount = DraftPrs.Count;
        ReviewCount = ReviewRequestedPrs.Count;
        TeamReviewCount = TeamReviewRequestedPrs.Count;
        HotfixCount = HotfixPrs.Count;
        HiddenCount = HiddenPrs.Count;
        OnHiddenPrsChanged?.Invoke();
    }

    public void RestoreItem(string key)
    {
        _settings.HiddenPrKeys.Remove(key);
        _settings.SnoozedPrs.Remove(key);
        _settings.ManuallyHiddenPrKeys.Remove(key);
        _settings.Save();
        _lastDisplaySignature = null;
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
            else if (item.IsDraftSectionPr)
            {
                DraftPrs.Add(item);
                DraftPrsCount = DraftPrs.Count;
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
            else if (item.IsDependabotPr)
            {
                DependabotPrs.Add(item);
                DependabotCount = DependabotPrs.Count;
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

    public void RefreshFromSnapshot(PollSnapshot snapshot)
    {
        // Triggered by a settings change, which can alter rendering in ways the row signature
        // does not capture — force a full rebuild.
        _lastDisplaySignature = null;
        UpdateFromSnapshot(snapshot);
        OnHiddenPrsChanged?.Invoke();
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

    public void SetUpdateAvailable(string version, string releaseUrl, string? releaseNotesUrl, string? releaseNotes)
    {
        LatestVersion = version;
        UpdateReleaseUrl = releaseUrl;
        UpdateReleaseNotesUrl = releaseNotesUrl ?? releaseUrl;
        UpdateAvailable = true;
    }

    public void OpenUpdateRelease()
    {
        if (!string.IsNullOrWhiteSpace(UpdateReleaseUrl)
            && Uri.TryCreate(UpdateReleaseUrl, UriKind.Absolute, out var uri)
            && uri.Scheme == Uri.UriSchemeHttps)
            Process.Start(new ProcessStartInfo(UpdateReleaseUrl) { UseShellExecute = true });
    }

    public void ViewChangelog()
    {
        var url = string.IsNullOrWhiteSpace(UpdateReleaseNotesUrl) ? UpdateReleaseUrl : UpdateReleaseNotesUrl;
        if (!string.IsNullOrWhiteSpace(url)
            && Uri.TryCreate(url, UriKind.Absolute, out var uri)
            && uri.Scheme == Uri.UriSchemeHttps)
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
    }

    public Task<UpdateChangelogResult?> GetUpdateChangelogAsync(CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(LatestVersion))
            return Task.FromResult<UpdateChangelogResult?>(null);

        return _updateService.GetRelevantChangelogAsync(
            UpdateService.GetCurrentAppVersionText(),
            LatestVersion,
            cancellationToken);
    }

    public async Task DownloadAndInstallUpdateAsync()
    {
        if (_isDownloadingUpdate || _updateReadyToInstall || string.IsNullOrWhiteSpace(LatestVersion))
            return;

        IsDownloadingUpdate = true;
        UpdateDownloadFailed = false;
        DownloadProgress = 0;

        try
        {
            var progress = new Progress<int>(p =>
            {
                System.Windows.Application.Current.Dispatcher.Invoke(() => DownloadProgress = p);
            });

            _tempExePath = await _updateService.DownloadUpdateAsync(LatestVersion, progress);

            System.Windows.Application.Current.Dispatcher.Invoke(() =>
            {
                IsDownloadingUpdate = false;
                UpdateReadyToInstall = true;
            });
            _notificationService.Notify("Update ready", $"PR Monitor v{LatestVersion} downloaded — click the banner to restart.");
        }
        catch (OperationCanceledException)
        {
            System.Windows.Application.Current.Dispatcher.Invoke(() => IsDownloadingUpdate = false);
        }
        catch (Exception ex)
        {
            System.Windows.Application.Current.Dispatcher.Invoke(() =>
            {
                IsDownloadingUpdate = false;
                UpdateDownloadFailed = true;
            });
            _notificationService.Notify("Update failed", $"Could not download PR Monitor v{LatestVersion}: {ex.Message}\nClick the banner to open the release page and download manually.");
        }
    }

    public void OpenReleasePageForManualDownload()
    {
        var url = string.IsNullOrWhiteSpace(UpdateReleaseNotesUrl) ? UpdateReleaseUrl : UpdateReleaseNotesUrl;
        if (!string.IsNullOrWhiteSpace(url)
            && Uri.TryCreate(url, UriKind.Absolute, out var uri)
            && uri.Scheme == Uri.UriSchemeHttps)
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
    }

    public void RestartToInstallUpdate()
    {
        if (string.IsNullOrWhiteSpace(_tempExePath) || !_updateReadyToInstall)
            return;

        _updateService.StartUpdateProcess(_tempExePath);
        System.Windows.Application.Current.Shutdown();
    }

    // ── Internals ───────────────────────────────────────────────────

    internal void UpdateFromSnapshot(PollSnapshot snapshot)
    {
        // Auto-restore snoozed PRs whose timer has expired
        var wakingKeys = _settings.SnoozedPrs
            .Where(kv => kv.Value != DateTimeOffset.MaxValue && kv.Value <= DateTimeOffset.UtcNow)
            .Select(kv => kv.Key)
            .ToList();
        foreach (var k in wakingKeys)
            RestoreItem(k);

        var hidden = _settings.HiddenPrKeys;
        var showStacks = _settings.ShowStackRelations;
        var (chains, parentIsMine) = BuildStackContext(snapshot);
        var stacked = new List<PrItemViewModel>();

        // Rows are built off to the side first: applying them to the bound collections tears down
        // and regenerates every row's visual tree, so it is skipped when nothing visibly changed.
        var autoMerge = new List<PrItemViewModel>();
        var myPrs = new List<PrItemViewModel>();
        var review = new List<PrItemViewModel>();
        var teamReview = new List<PrItemViewModel>();
        var dependabot = new List<PrItemViewModel>();
        var hotfix = new List<PrItemViewModel>();
        var drafts = new List<PrItemViewModel>();
        var hiddenItems = new List<PrItemViewModel>();

        PrItemViewModel Make(PullRequestInfo pr, bool isAutoMerge = false, bool isMyPr = false, bool isHotfix = false,
            bool isTeamReview = false, bool isDependabot = false, bool isDraftSection = false, string snoozedUntilText = "") =>
            PrItemViewModel.From(pr, isAutoMerge: isAutoMerge, isMyPr: isMyPr, isHotfix: isHotfix, isTeamReview: isTeamReview,
                isDependabot: isDependabot, isDraftSection: isDraftSection, snoozedUntilText: snoozedUntilText,
                showStackRelations: showStacks, stackChainTooltip: chains.GetValueOrDefault(pr.Key, ""),
                stackParentIsMine: parentIsMine.Contains(pr.Key));

        // Stacked PRs are collected in their own section instead of their regular one.
        void Route(List<PrItemViewModel> section, PullRequestInfo pr, PrItemViewModel item)
        {
            if (showStacks && pr.IsStacked) stacked.Add(item);
            else section.Add(item);
        }

        foreach (var pr in snapshot.AutoMergePrs)
        {
            if (!hidden.Contains(pr.Key))
                Route(autoMerge, pr, Make(pr, isAutoMerge: true));
        }

        foreach (var pr in snapshot.MyPrs)
        {
            if (!hidden.Contains(pr.Key))
                Route(myPrs, pr, Make(pr, isMyPr: true));
        }

        foreach (var pr in snapshot.ReviewRequestedPrs)
        {
            if (!hidden.Contains(pr.Key))
                Route(review, pr, Make(pr));
        }

        foreach (var pr in snapshot.TeamReviewRequestedPrs)
        {
            if (!hidden.Contains(pr.Key))
                Route(teamReview, pr, Make(pr, isTeamReview: true));
        }

        foreach (var pr in snapshot.DependabotPrs)
        {
            if (!hidden.Contains(pr.Key))
                Route(dependabot, pr, Make(pr, isDependabot: true));
        }

        foreach (var pr in snapshot.HotfixPrs)
        {
            if (!hidden.Contains(pr.Key))
                Route(hotfix, pr, Make(pr, isHotfix: true));
        }

        foreach (var pr in snapshot.DraftPrs)
        {
            if (!hidden.Contains(pr.Key))
                Route(drafts, pr, Make(pr, isDraftSection: true));
        }

        var orderedStacked = OrderStackSection(stacked).ToList();

        // Rebuild hidden list from all PRs in this snapshot
        foreach (var x in snapshot.AutoMergePrs.Select(p => (pr: p, isAm: true, isMyPr: false, isHotfix: false, isTeamReview: false, isDependabot: false, isDraftSection: false))
                     .Concat(snapshot.MyPrs.Select(p => (pr: p, isAm: false, isMyPr: true, isHotfix: false, isTeamReview: false, isDependabot: false, isDraftSection: false)))
                     .Concat(snapshot.DraftPrs.Select(p => (pr: p, isAm: false, isMyPr: false, isHotfix: false, isTeamReview: false, isDependabot: false, isDraftSection: true)))
                     .Concat(snapshot.ReviewRequestedPrs.Select(p => (pr: p, isAm: false, isMyPr: false, isHotfix: false, isTeamReview: false, isDependabot: false, isDraftSection: false)))
                     .Concat(snapshot.TeamReviewRequestedPrs.Select(p => (pr: p, isAm: false, isMyPr: false, isHotfix: false, isTeamReview: true, isDependabot: false, isDraftSection: false)))
                     .Concat(snapshot.HotfixPrs.Select(p => (pr: p, isAm: false, isMyPr: false, isHotfix: true, isTeamReview: false, isDependabot: false, isDraftSection: false)))
                     .Concat(snapshot.DependabotPrs.Select(p => (pr: p, isAm: false, isMyPr: false, isHotfix: false, isTeamReview: false, isDependabot: true, isDraftSection: false)))
                     .DistinctBy(x => x.pr.Key)
                     .Where(x => hidden.Contains(x.pr.Key) && _settings.SnoozedPrs.ContainsKey(x.pr.Key)))
        {
            hiddenItems.Add(PrItemViewModel.From(x.pr, isAutoMerge: x.isAm, isMyPr: x.isMyPr, isHotfix: x.isHotfix, isTeamReview: x.isTeamReview, isDependabot: x.isDependabot, isDraftSection: x.isDraftSection,
                snoozedUntilText: FormatSnoozedUntil(_settings.SnoozedPrs.GetValueOrDefault(x.pr.Key, DateTimeOffset.MaxValue)),
                showStackRelations: showStacks,
                stackChainTooltip: chains.GetValueOrDefault(x.pr.Key, ""),
                stackParentIsMine: parentIsMine.Contains(x.pr.Key)));
        }

        var signature = BuildDisplaySignature(
            autoMerge, myPrs, review, teamReview, dependabot, hotfix, drafts, orderedStacked, hiddenItems);

        if (signature != _lastDisplaySignature)
        {
            _lastDisplaySignature = signature;
            Replace(AutoMergePrs, autoMerge);
            Replace(MyPrs, myPrs);
            Replace(ReviewRequestedPrs, review);
            Replace(TeamReviewRequestedPrs, teamReview);
            Replace(DependabotPrs, dependabot);
            Replace(HotfixPrs, hotfix);
            Replace(DraftPrs, drafts);
            Replace(StackedPrs, orderedStacked);
            Replace(HiddenPrs, hiddenItems);
        }

        AutoMergeCount = AutoMergePrs.Count;
        MyPrsCount = MyPrs.Count;
        DraftPrsCount = DraftPrs.Count;
        ReviewCount = ReviewRequestedPrs.Count;
        TeamReviewCount = TeamReviewRequestedPrs.Count;
        HotfixCount = HotfixPrs.Count;
        DependabotCount = DependabotPrs.Count;
        StackedCount = StackedPrs.Count;
        HiddenCount = HiddenPrs.Count;
        LastUpdated = DateTime.Now.ToString("HH:mm:ss");

        OnPropertyChanged(nameof(TotalPrCount));
        OnPropertyChanged(nameof(IsEmptyState));

        var isEmptyNow = TotalPrCount == 0;
        if (isEmptyNow && !_wasEmpty)
            EmptyStateHeadline = EmptyStateHeadlines[_emptyStateRandom.Next(EmptyStateHeadlines.Length)];
        _wasEmpty = isEmptyNow;

        HasLoadedOnce = true;
    }

    /// <summary>
    /// Concatenation of everything rendered in the PR list. Identical signatures mean a rebuild
    /// of the bound collections would produce visually identical rows, so it can be skipped.
    /// </summary>
    private static string BuildDisplaySignature(params IReadOnlyList<PrItemViewModel>[] sections)
    {
        var builder = new System.Text.StringBuilder();
        foreach (var section in sections)
        {
            foreach (var item in section)
            {
                builder.Append(item.DisplaySignature);
                builder.Append('\u001E');
            }
            builder.Append('\u001D');
        }
        return builder.ToString();
    }

    private static void Replace(ObservableCollection<PrItemViewModel> target, List<PrItemViewModel> items)
    {
        target.Clear();
        foreach (var item in items)
            target.Add(item);
    }

    private static void OpenUrl(string url)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps)
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
    }

    /// <summary>Per-PR stack chain tooltips and the set of PRs whose stack parent is authored by me.</summary>
    private (Dictionary<string, string> Chains, HashSet<string> ParentIsMine) BuildStackContext(PollSnapshot snapshot)
    {
        var all = snapshot.AutoMergePrs
            .Concat(snapshot.MyPrs).Concat(snapshot.DraftPrs).Concat(snapshot.ReviewRequestedPrs)
            .Concat(snapshot.TeamReviewRequestedPrs).Concat(snapshot.HotfixPrs).Concat(snapshot.DependabotPrs)
            .DistinctBy(p => p.Key, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var me = _settings.GitHubUsername;
        var parentIsMine = all
            .Where(p => !string.IsNullOrWhiteSpace(me)
                     && p.StackParentAuthor.Equals(me, StringComparison.OrdinalIgnoreCase))
            .Select(p => p.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var chains = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var group in all.Where(p => p.IsStacked)
                                 .GroupBy(p => p.StackRootKey ?? p.Key, StringComparer.OrdinalIgnoreCase))
        {
            var members = group.OrderBy(p => p.StackDepth).ThenBy(p => p.Number).ToList();
            foreach (var pr in members)
                chains[pr.Key] = BuildStackChainTooltip(pr, members);
        }

        return (chains, parentIsMine);
    }

    internal static string BuildStackChainTooltip(PullRequestInfo pr, IReadOnlyList<PullRequestInfo> members)
    {
        var lines = new List<string> { $"Stack ({members.Count} PRs):" };
        foreach (var m in members)
        {
            var marker = m.Key.Equals(pr.Key, StringComparison.OrdinalIgnoreCase) ? "\u25b8" : " ";
            var state = m.IsDraft ? "Draft" : m.HasConflicts ? "Conflicts" : m.CIState.ToString();
            lines.Add($" {marker} {m.StackDepth + 1}/{members.Count}  #{m.Number} {m.Author} — {state}");
        }
        return string.Join(Environment.NewLine, lines);
    }

    /// <summary>Groups the Stacks section per stack (first seen first), bottom PR first within a stack.</summary>
    internal static List<PrItemViewModel> OrderStackSection(IReadOnlyList<PrItemViewModel> items)
    {
        var rootOrder = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in items)
        {
            if (!rootOrder.ContainsKey(item.StackRootKey))
                rootOrder[item.StackRootKey] = rootOrder.Count;
        }

        var ordered = items
            .OrderBy(i => rootOrder[i.StackRootKey])
            .ThenBy(i => i.StackDepth)
            .ThenBy(i => i.Number)
            .ToList();

        string? previousRoot = null;
        foreach (var item in ordered)
        {
            var isNewGroup = previousRoot is null
                || !previousRoot.Equals(item.StackRootKey, StringComparison.OrdinalIgnoreCase);
            item.IsStackGroupStart = isNewGroup;
            item.ShowStackGroupSeparator = isNewGroup && previousRoot is not null;
            previousRoot = item.StackRootKey;
        }
        return ordered;
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

    internal static string FormatSnoozedUntil(DateTimeOffset until)
    {
        if (until == DateTimeOffset.MaxValue) return "∞";
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
    public bool HasConflicts { get; init; }
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
    public bool IsDependabotPr { get; init; }
    public bool IsDraftSectionPr { get; init; }
    public bool IsDraft { get; init; }
    public string HeadRefName { get; init; } = "";
    public string HeadCommitSha { get; init; } = "";

    // ── Stacked-PR relations ──
    public int StackDepth { get; init; }
    public int StackSize { get; init; } = 1;
    public string StackRootKey { get; init; } = "";
    public int StackParentNumber { get; init; }
    public string StackParentUrl { get; init; } = "";
    public bool IsBlockedByStack { get; init; }

    /// <summary>True when the PR directly below this one in the stack is authored by the current user.</summary>
    public bool StackParentIsMine { get; init; }

    /// <summary>Pre-rendered overview of the whole stack, appended to the tooltip.</summary>
    public string StackChainTooltip { get; init; } = "";

    /// <summary>Set for the first row of every stack but the first one in the Stacks section.</summary>
    public bool ShowStackGroupSeparator { get; internal set; }

    /// <summary>True for the topmost visible row of a stack group in the Stacks section.</summary>
    public bool IsStackGroupStart { get; internal set; }

    /// <summary>Row margin in the Stacks section: every row but the group's first is indented one level.</summary>
    public Thickness StackIndentMargin => new(IsStackGroupStart ? 0 : 14, 2, 0, 2);

    /// <summary>Whether stack grouping/indentation is enabled in settings.</summary>
    public bool ShowStackRelations { get; init; } = true;

    /// <summary>True when this PR belongs to a stack of two or more open PRs.</summary>
    public bool IsStacked => StackSize > 1;

    /// <summary>1-based position of this PR within its stack.</summary>
    public int StackPosition => StackDepth + 1;

    /// <summary>Whether the stack visuals (indent, badge) should be rendered.</summary>
    public bool ShowStackIndicator => IsStacked && ShowStackRelations;

    /// <summary>Suffix appended to the repository line, e.g. " · stack 2/3 · waits on #41 (you)".</summary>
    public string StackBadgeText
    {
        get
        {
            if (!ShowStackIndicator) return "";
            var badge = $" · stack {StackPosition}/{StackSize}";
            if (IsBlockedByStack && StackParentNumber > 0)
                badge += $" · waits on #{StackParentNumber}{(StackParentIsMine ? " (you)" : "")}";
            return badge;
        }
    }

    /// <summary>Whether the "Open parent PR" action is available.</summary>
    public bool CanOpenStackParent => IsBlockedByStack && !string.IsNullOrWhiteSpace(StackParentUrl);

    public bool HasAutoMerge { get; init; }
    public bool IsApproved { get; init; }
    public IReadOnlyList<string> ReviewerLogins { get; init; } = [];
    public IReadOnlyDictionary<string, ReviewState> ReviewerStates { get; init; } = new Dictionary<string, ReviewState>();
    public bool HasNonCopilotReviewer => ReviewerLogins.Count > 0;
    public bool IsOwnPr => IsMyPr || IsAutoMergePr || IsHotfixPr || IsDraftSectionPr;
    public bool ShowNoReviewerWarning => IsOwnPr && !HasNonCopilotReviewer;
    public string ReviewerTooltip => HasNonCopilotReviewer
        ? string.Join(", ", ReviewerLogins)
        : "No reviewer assigned";

    /// <summary>Latest review state for a reviewer login; defaults to Pending when not yet recorded.</summary>
    public ReviewState StateOf(string login) => ReviewerStates.TryGetValue(login, out var s) ? s : ReviewState.Pending;

    /// <summary>Whether any assigned reviewer has requested changes.</summary>
    public bool HasChangesRequested => ReviewerLogins.Any(l => StateOf(l) == ReviewState.ChangesRequested);

    /// <summary>Whether at least one reviewer is assigned and none of them has responded yet.</summary>
    public bool IsReviewPending => HasNonCopilotReviewer && ReviewerLogins.All(l => StateOf(l) == ReviewState.Pending);

    /// <summary>Whether any assigned reviewer's latest state is Commented (no approval/changes-requested decision).</summary>
    public bool HasCommentedOnly => ReviewerLogins.Any(l => StateOf(l) == ReviewState.Commented);

    /// <summary>Show the changes-requested icon: highest-priority reviewer-state icon after unresolved comments.</summary>
    public bool ShowChangesRequestedIcon => IsOwnPr && !HasUnresolvedReviewComments && HasChangesRequested;

    /// <summary>Show the review-pending icon: reviewer(s) assigned but nobody has responded yet.</summary>
    public bool ShowReviewPendingIcon => IsOwnPr && !HasUnresolvedReviewComments && !HasChangesRequested && IsReviewPending;

    /// <summary>Show the commented icon: a reviewer left feedback without approving or requesting changes.</summary>
    public bool ShowCommentedIcon => IsOwnPr && !HasUnresolvedReviewComments && !HasChangesRequested && !IsReviewPending && HasCommentedOnly && !IsApproved;
    public string PrTooltip
    {
        get
        {
            var parts = new System.Collections.Generic.List<string>();
            parts.Add($"Opened: {CreatedAtFormatted}");
            parts.Add($"CI: {CIState}");
            if (IsStacked)
            {
                parts.Add(string.IsNullOrEmpty(StackChainTooltip)
                    ? IsBlockedByStack
                        ? $"Stack: {StackPosition} of {StackSize} — waiting on #{StackParentNumber}"
                        : $"Stack: {StackPosition} of {StackSize} — bottom of the stack"
                    : StackChainTooltip);
            }
            if (HasConflicts)
                parts.Add("Merge conflicts");
            if (IsOwnPr)
                parts.Add(HasNonCopilotReviewer
                    ? $"Reviewers: {string.Join(", ", ReviewerLogins.Select(l => $"{l} ({StateOf(l).ToDisplayString()})"))}"
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
    public bool CanEnableAutoMerge => IsOwnPr && !IsDraft && !HasAutoMerge;

    /// <summary>Show the approved checkmark icon: PR is approved but has no unresolved review comments (comments take priority).</summary>
    public bool ShowApprovedIcon => IsApproved && !HasUnresolvedReviewComments && !HasChangesRequested;

    /// <summary>
    /// CI state used for the indicator: always Unknown (grey) for draft PRs and Failure when the PR
    /// has merge conflicts. A PR that only waits for an open parent PR in its stack keeps its own
    /// CI colour, so a healthy stacked PR still shows green.
    /// </summary>
    public CIState EffectiveCIState =>
        HasConflicts ? CIState.Failure
        : IsDraft ? CIState.Unknown
        : CIState;

    public void OpenInBrowser()
    {
        if (Uri.TryCreate(Url, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps)
            Process.Start(new ProcessStartInfo(Url) { UseShellExecute = true });
    }

    /// <summary>
    /// Every value this row renders, joined into one string. Two rows with equal signatures are
    /// visually identical, so the bound collections can be left untouched.
    /// </summary>
    internal string DisplaySignature => string.Join('\u001F',
        Key, Repository, Title, Author, TimeAgo, SnoozedUntilText, CIIcon, EffectiveCIState,
        StackBadgeText, IsStackGroupStart, ShowStackGroupSeparator,
        HasUnresolvedReviewComments, ShowChangesRequestedIcon, ShowNoReviewerWarning,
        ShowReviewPendingIcon, ShowCommentedIcon, ShowApprovedIcon, PrTooltip);

    public static PrItemViewModel From(PullRequestInfo pr, bool isAutoMerge = false, bool isMyPr = false, bool isHotfix = false, bool isTeamReview = false, bool isDependabot = false, bool isDraftSection = false, string snoozedUntilText = "", bool showStackRelations = true, string stackChainTooltip = "", bool stackParentIsMine = false) => new()
    {
        Key = pr.Key,
        Repository = pr.Repository,
        Title = pr.Title,
        Url = pr.Url,
        Author = pr.Author,
        Number = pr.Number,
        CIState = pr.CIState,
        HasConflicts = pr.HasConflicts,
        UnresolvedReviewCommentCount = pr.UnresolvedReviewCommentCount,
        IsAutoMergePr = isAutoMerge,
        IsMyPr = isMyPr,
        IsHotfixPr = isHotfix,
        IsTeamReviewPr = isTeamReview,
        IsDependabotPr = isDependabot,
        IsDraftSectionPr = isDraftSection,
        IsDraft = pr.IsDraft,
        HeadRefName = pr.HeadRefName,
        HeadCommitSha = pr.HeadCommitSha,
        StackDepth = pr.StackDepth,
        StackSize = pr.StackSize,
        StackRootKey = pr.StackRootKey ?? pr.Key,
        StackParentNumber = pr.StackParentNumber,
        StackParentUrl = pr.StackParentUrl,
        StackParentIsMine = stackParentIsMine,
        StackChainTooltip = stackChainTooltip,
        IsBlockedByStack = pr.IsBlockedByStack,
        ShowStackRelations = showStackRelations,
        HasAutoMerge = pr.HasAutoMerge,
        IsApproved = pr.IsApproved,
        ReviewerLogins = pr.ReviewerLogins,
        ReviewerStates = pr.ReviewerStates,
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
