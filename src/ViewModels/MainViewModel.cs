using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using PrBot.Models;
using PrBot.Services;

namespace PrBot.ViewModels;

/// <summary>
/// ViewModel for the floating PR monitor window.
/// Binds to poll data and exposes observable collections.
/// </summary>
public sealed class MainViewModel : INotifyPropertyChanged
{
    private PollingService? _polling;

    public ObservableCollection<PrItemViewModel> AutoMergePrs { get; } = [];
    public ObservableCollection<PrItemViewModel> ReviewRequestedPrs { get; } = [];

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
        AutoMergePrs.Clear();
        foreach (var pr in snapshot.AutoMergePrs)
            AutoMergePrs.Add(PrItemViewModel.From(pr));

        ReviewRequestedPrs.Clear();
        foreach (var pr in snapshot.ReviewRequestedPrs)
            ReviewRequestedPrs.Add(PrItemViewModel.From(pr));

        AutoMergeCount = snapshot.AutoMergePrs.Count;
        ReviewCount = snapshot.ReviewRequestedPrs.Count;
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
}

/// <summary>
/// View model for a single PR row in the list.
/// </summary>
public sealed class PrItemViewModel
{
    public required string Repository { get; init; }
    public required string Title { get; init; }
    public required string Url { get; init; }
    public required string Author { get; init; }
    public required string TimeAgo { get; init; }
    public required string CIIcon { get; init; }
    public required CIState CIState { get; init; }
    public int Number { get; init; }

    public void OpenInBrowser() =>
        Process.Start(new ProcessStartInfo(Url) { UseShellExecute = true });

    public static PrItemViewModel From(PullRequestInfo pr) => new()
    {
        Repository = pr.Repository,
        Title = pr.Title,
        Url = pr.Url,
        Author = pr.Author,
        Number = pr.Number,
        CIState = pr.CIState,
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
