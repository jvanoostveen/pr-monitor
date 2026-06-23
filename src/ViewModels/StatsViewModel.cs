using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using PrMonitor.Settings;

namespace PrMonitor.ViewModels;

/// <summary>One row in the per-author breakdown table for Reviews requested.</summary>
public sealed class AuthorBreakdownRow
{
    public required string Author { get; init; }
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

    public IReadOnlyList<AuthorBreakdownRow>? AuthorBreakdown { get; init; }
    public bool HasBreakdown => AuthorBreakdown is { Count: > 0 };

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

    public StatsViewModel(StatisticsStore store)
    {
        _store = store;
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

        // Preserve user's expanded state across refreshes.
        var expandedLabels = Rows.Where(r => r.IsExpanded).Select(r => r.Label).ToHashSet();

        Rows.Clear();
        AddRow("Reviews requested", StatMetric.ReviewsRequested, day, week, month, total,
            BuildAuthorBreakdown(day, week, month, total));
        AddRow("Reviews completed", StatMetric.ReviewsCompleted, day, week, month, total);
        AddRow("PRs opened",        StatMetric.OwnPrsOpened,     day, week, month, total);
        AddRow("PRs merged",        StatMetric.OwnPrsMerged,     day, week, month, total);
        AddRow("CI failures",       StatMetric.CiFailures,       day, week, month, total);
        AddRow("Flaky reruns",      StatMetric.FlakyReruns,      day, week, month, total);
        AddRow("Real failures",     StatMetric.RealFailures,     day, week, month, total);

        foreach (var row in Rows)
            if (expandedLabels.Contains(row.Label))
                row.IsExpanded = true;

        LastUpdated = DateTime.Now.ToString("HH:mm:ss");
    }

    private static IReadOnlyList<AuthorBreakdownRow>? BuildAuthorBreakdown(
        DayStat day, DayStat week, DayStat month, DayStat total)
    {
        if (total.ReviewsRequestedByAuthor is not { Count: > 0 } authorTotals)
            return null;

        return authorTotals.Keys
            .Select(a => new AuthorBreakdownRow
            {
                Author = a,
                Today = day.ReviewsRequestedByAuthor?.GetValueOrDefault(a) ?? 0,
                Week  = week.ReviewsRequestedByAuthor?.GetValueOrDefault(a) ?? 0,
                Month = month.ReviewsRequestedByAuthor?.GetValueOrDefault(a) ?? 0,
                Total = authorTotals[a],
            })
            .OrderByDescending(r => r.Total)
            .ThenBy(r => r.Author, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private void AddRow(string label, StatMetric metric,
        DayStat day, DayStat week, DayStat month, DayStat total,
        IReadOnlyList<AuthorBreakdownRow>? authorBreakdown = null)
    {
        Rows.Add(new StatRowViewModel
        {
            Label = label,
            Today = day.Get(metric),
            Week  = week.Get(metric),
            Month = month.Get(metric),
            Total = total.Get(metric),
            AuthorBreakdown = authorBreakdown,
        });
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
