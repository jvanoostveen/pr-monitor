using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using PrBot.Models;
using PrBot.Services;
using PrBot.Settings;

namespace PrBot.ViewModels;

/// <summary>
/// ViewModel for the floating PR monitor window.
/// Binds to poll data and exposes observable collections.
/// </summary>
public sealed class MainViewModel : INotifyPropertyChanged
{
    private readonly AppSettings _settings;
    private PollingService? _polling;

    public MainViewModel(AppSettings settings)
    {
        _settings = settings;
        _hiddenCount = settings.HiddenPrKeys.Count;
    }

    public ObservableCollection<PrItemViewModel> AutoMergePrs { get; } = [];
    public ObservableCollection<PrItemViewModel> ReviewRequestedPrs { get; } = [];
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

    public void ToggleAutoMergeExpanded() => AutoMergeExpanded = !AutoMergeExpanded;
    public void ToggleReviewExpanded() => ReviewExpanded = !ReviewExpanded;
    public void ToggleHotfixExpanded() => HotfixExpanded = !HotfixExpanded;
    public void ToggleLaterExpanded() => LaterExpanded = !LaterExpanded;

    public void HideItem(string key)
    {
        _settings.HiddenPrKeys.Add(key);
        _settings.Save();

        // Find item in active lists, move it to HiddenPrs immediately
        var item = FindAndRemove(HotfixPrs, key)
                ?? FindAndRemove(AutoMergePrs, key)
                ?? FindAndRemove(ReviewRequestedPrs, key);
        if (item is not null)
        {
            var wasEmpty = HiddenPrs.Count == 0;
            HiddenPrs.Add(item);
            // Only auto-expand Later if it was empty before — don't override user's collapsed state
            if (wasEmpty)
                LaterExpanded = true;
        }

        AutoMergeCount = AutoMergePrs.Count;
        ReviewCount = ReviewRequestedPrs.Count;
        HotfixCount = HotfixPrs.Count;
        HiddenCount = HiddenPrs.Count;
    }

    public void RestoreItem(string key)
    {
        _settings.HiddenPrKeys.Remove(key);
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
            else
            {
                ReviewRequestedPrs.Add(item);
                ReviewCount = ReviewRequestedPrs.Count;
            }
        }
        HiddenCount = HiddenPrs.Count;
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
                IsRefreshing = false;
                UpdateFromSnapshot(snapshot);
            });
        };
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

    // ── Internals ───────────────────────────────────────────────────

    private void UpdateFromSnapshot(PollSnapshot snapshot)
    {
        // All PR keys seen in this poll
        var allKeys = snapshot.AutoMergePrs.Select(p => p.Key)
            .Concat(snapshot.ReviewRequestedPrs.Select(p => p.Key))
            .Concat(snapshot.HotfixPrs.Select(p => p.Key))
            .ToHashSet();

        // Remove hidden keys for PRs that no longer exist
        var stale = _settings.HiddenPrKeys.Where(k => !allKeys.Contains(k)).ToList();
        foreach (var k in stale) _settings.HiddenPrKeys.Remove(k);
        if (stale.Count > 0) _settings.Save();

        var hidden = _settings.HiddenPrKeys;

        AutoMergePrs.Clear();
        foreach (var pr in snapshot.AutoMergePrs)
        {
            if (!hidden.Contains(pr.Key))
                AutoMergePrs.Add(PrItemViewModel.From(pr, isAutoMerge: true));
        }

        ReviewRequestedPrs.Clear();
        foreach (var pr in snapshot.ReviewRequestedPrs)
        {
            if (!hidden.Contains(pr.Key))
                ReviewRequestedPrs.Add(PrItemViewModel.From(pr, isAutoMerge: false));
        }

        HotfixPrs.Clear();
        foreach (var pr in snapshot.HotfixPrs)
        {
            if (!hidden.Contains(pr.Key))
                HotfixPrs.Add(PrItemViewModel.From(pr, isHotfix: true));
        }

        // Rebuild hidden list from all PRs in this snapshot
        HiddenPrs.Clear();
        foreach (var pr in snapshot.AutoMergePrs.Select(p => (pr: p, isAm: true, isHotfix: false))
                     .Concat(snapshot.ReviewRequestedPrs.Select(p => (pr: p, isAm: false, isHotfix: false)))
                     .Concat(snapshot.HotfixPrs.Select(p => (pr: p, isAm: false, isHotfix: true)))
                     .DistinctBy(x => x.pr.Key)
                     .Where(x => hidden.Contains(x.pr.Key)))
        {
            HiddenPrs.Add(PrItemViewModel.From(pr.pr, isAutoMerge: pr.isAm, isHotfix: pr.isHotfix));
        }

        AutoMergeCount = AutoMergePrs.Count;
        ReviewCount = ReviewRequestedPrs.Count;
        HotfixCount = HotfixPrs.Count;
        HiddenCount = HiddenPrs.Count;
        LastUpdated = DateTime.Now.ToString("HH:mm:ss");
    }

    private static void OpenUrl(string url) =>
        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });

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
    public required string CIIcon { get; init; }
    public required CIState CIState { get; init; }
    public int Number { get; init; }
    public bool IsAutoMergePr { get; init; }
    public bool IsHotfixPr { get; init; }

    public void OpenInBrowser() =>
        Process.Start(new ProcessStartInfo(Url) { UseShellExecute = true });

    public static PrItemViewModel From(PullRequestInfo pr, bool isAutoMerge = false, bool isHotfix = false) => new()
    {
        Key = pr.Key,
        Repository = pr.Repository,
        Title = pr.Title,
        Url = pr.Url,
        Author = pr.Author,
        Number = pr.Number,
        CIState = pr.CIState,
        IsAutoMergePr = isAutoMerge,
        IsHotfixPr = isHotfix,
        CIIcon = pr.CIState switch
        {
            CIState.Success => "✅",
            CIState.Failure => "❌",
            CIState.Pending => "⏳",
            CIState.Error => "⚠️",
            _ => "❔",
        },
        TimeAgo = FormatTimeAgo(pr.CreatedAt),
    };

    private static string FormatTimeAgo(DateTimeOffset created)
    {
        var span = DateTimeOffset.Now - created;
        if (span.TotalMinutes < 1) return "just now";
        if (span.TotalHours < 1) return $"{(int)span.TotalMinutes}m ago";
        if (span.TotalDays < 1) return $"{(int)span.TotalHours}h ago";
        if (span.TotalDays < 30) return $"{(int)span.TotalDays}d ago";
        return created.ToString("MMM dd");
    }
}
