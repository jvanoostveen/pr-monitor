using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using PrMonitor.Models;
using PrMonitor.Settings;

namespace PrMonitor.ViewModels;

/// <summary>One row in the per-author breakdown table for Reviews requested.</summary>
public sealed class AuthorBreakdownRow
{
    public required string Author { get; init; }
    public required string DisplayName { get; init; }
    public int Today { get; init; }
    public int Week { get; init; }
    public int Month { get; init; }
    public int Total { get; init; }
}

/// <summary>
/// A single statistics table row: a metric and its counts per period.
/// Optionally carries a per-author breakdown and supports inline expansion.
/// </summary>
public sealed class StatRowViewModel : INotifyPropertyChanged
{
    public required string Label { get; init; }
    public int Today { get; init; }
    public int Week { get; init; }
    public int Month { get; init; }
    public int Total { get; init; }

    /// <summary>All non-hidden author rows (includes inactive ones with Week==0).</summary>
    public IReadOnlyList<AuthorBreakdownRow>? AllAuthorBreakdown { get; init; }

    /// <summary>Only rows with Week > 0 (active this week).</summary>
    public IReadOnlyList<AuthorBreakdownRow>? ActiveAuthorBreakdown { get; init; }

    /// <summary>Currently visible rows depending on ShowAll toggle.</summary>
    public IReadOnlyList<AuthorBreakdownRow>? VisibleAuthorBreakdown => _showAll ? AllAuthorBreakdown : ActiveAuthorBreakdown;

    public bool HasBreakdown => (AllAuthorBreakdown?.Count ?? 0) > 0;

    public int InactiveReviewerCount => (AllAuthorBreakdown?.Count ?? 0) - (ActiveAuthorBreakdown?.Count ?? 0);
    public bool HasInactiveReviewers => InactiveReviewerCount > 0;

    private bool _showAll;
    public bool ShowAll
    {
        get => _showAll;
        set
        {
            if (_showAll == value) return;
            _showAll = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(VisibleAuthorBreakdown));
            OnPropertyChanged(nameof(ToggleInactiveLabel));
        }
    }

    public string ToggleInactiveLabel => _showAll ? "Show active only" : $"Show {InactiveReviewerCount} inactive";

    private bool _isExpanded;
    public bool IsExpanded
    {
        get => _isExpanded;
        set { if (_isExpanded != value) { _isExpanded = value; OnPropertyChanged(); } }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>
/// ViewModel for the statistics window. Builds a table of activity metrics with
/// per-period columns (Today / This week / This month / Total) from the store.
/// </summary>
public sealed class StatsViewModel : INotifyPropertyChanged
{
    private readonly StatisticsStore _store;
    private readonly IReadOnlyDictionary<string, string> _memberNames;
    private readonly AppSettings? _settings;
    private HashSet<string> _hiddenReviewers;

    public StatsViewModel(StatisticsStore store, AppSettings? settings = null)
    {
        _store = store;
        _settings = settings;
        _memberNames = settings?.OrgMembersCache
            .Where(m => !string.IsNullOrWhiteSpace(m.Name))
            .ToDictionary(m => m.Login, m => m.Name!, StringComparer.OrdinalIgnoreCase)
            ?? new Dictionary<string, string>();
        _hiddenReviewers = settings?.HiddenStatReviewRequesters
            .ToHashSet(StringComparer.OrdinalIgnoreCase)
            ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        Refresh();
    }

    public ObservableCollection<StatRowViewModel> Rows { get; } = [];

    private string _lastUpdated = "";
    public string LastUpdated
    {
        get => _lastUpdated;
        private set => SetField(ref _lastUpdated, value);
    }

    /// <summary>Rebuild all rows from the current store contents.</summary>
    public void Refresh()
    {
        var today = DateOnly.FromDateTime(DateTime.Today);
        var day = _store.ForDay(today);
        var week = _store.ForWeekOf(today);
        var month = _store.ForMonthOf(today);
        var total = _store.Total();

        // Preserve user's expanded and ShowAll state across refreshes.
        var expandedLabels = Rows.Where(r => r.IsExpanded).Select(r => r.Label).ToHashSet();
        var showAllLabels = Rows.Where(r => r.ShowAll).Select(r => r.Label).ToHashSet();

        Rows.Clear();
        var (allBreakdown, activeBreakdown) = BuildAuthorBreakdown(day, week, month, total);
        AddRow("Reviews requested", StatMetric.ReviewsRequested, day, week, month, total,
            allBreakdown, activeBreakdown);
        AddRow("Reviews completed", StatMetric.ReviewsCompleted, day, week, month, total);
        AddRow("PRs opened",        StatMetric.OwnPrsOpened,     day, week, month, total);
        AddRow("PRs merged",        StatMetric.OwnPrsMerged,     day, week, month, total);
        AddRow("CI failures",       StatMetric.CiFailures,       day, week, month, total);
        AddRow("Flaky reruns",      StatMetric.FlakyReruns,      day, week, month, total);
        AddRow("Real failures",     StatMetric.RealFailures,     day, week, month, total);

        foreach (var row in Rows)
        {
            if (expandedLabels.Contains(row.Label))
                row.IsExpanded = true;
            if (showAllLabels.Contains(row.Label))
                row.ShowAll = true;
        }

        LastUpdated = DateTime.Now.ToString("HH:mm:ss");
    }

    private (IReadOnlyList<AuthorBreakdownRow>? all, IReadOnlyList<AuthorBreakdownRow>? active) BuildAuthorBreakdown(
        DayStat day, DayStat week, DayStat month, DayStat total)
    {
        if (total.ReviewsRequestedByAuthor is not { Count: > 0 } authorTotals)
            return (null, null);

        var all = authorTotals.Keys
            .Where(a => !_hiddenReviewers.Contains(a))
            .Select(a => new AuthorBreakdownRow
            {
                Author = a,
                DisplayName = _memberNames.TryGetValue(a, out var name) && !name.Equals(a, StringComparison.OrdinalIgnoreCase)
                    ? name
                    : a,
                Today = day.ReviewsRequestedByAuthor?.GetValueOrDefault(a) ?? 0,
                Week  = week.ReviewsRequestedByAuthor?.GetValueOrDefault(a) ?? 0,
                Month = month.ReviewsRequestedByAuthor?.GetValueOrDefault(a) ?? 0,
                Total = authorTotals[a],
            })
            .OrderByDescending(r => r.Total)
            .ThenBy(r => r.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var active = all.Where(r => r.Week > 0).ToList();
        return (all.Count > 0 ? all : null, active.Count > 0 ? active : null);
    }

    private void AddRow(string label, StatMetric metric,
        DayStat day, DayStat week, DayStat month, DayStat total,
        IReadOnlyList<AuthorBreakdownRow>? allBreakdown = null,
        IReadOnlyList<AuthorBreakdownRow>? activeBreakdown = null)
    {
        Rows.Add(new StatRowViewModel
        {
            Label = label,
            Today = day.Get(metric),
            Week  = week.Get(metric),
            Month = month.Get(metric),
            Total = total.Get(metric),
            AllAuthorBreakdown = allBreakdown,
            ActiveAuthorBreakdown = activeBreakdown,
        });
    }

    /// <summary>
    /// Permanently hides a reviewer from the breakdown. Persists to settings.
    /// </summary>
    public void HideReviewer(string login)
    {
        if (_settings is null) return;
        if (_hiddenReviewers.Contains(login)) return;

        _settings.HiddenStatReviewRequesters.Add(login);
        _hiddenReviewers = _settings.HiddenStatReviewRequesters
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        try { _settings.Save(); } catch { /* best-effort */ }
        Refresh();
    }

    // ── INotifyPropertyChanged ──────────────────────────────────────────

    public event PropertyChangedEventHandler? PropertyChanged;

    private void SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
