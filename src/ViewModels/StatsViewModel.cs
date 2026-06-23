using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using PrMonitor.Settings;

namespace PrMonitor.ViewModels;

/// <summary>
/// A single statistics table row: a metric and its counts per period.
/// </summary>
public sealed class StatRowViewModel
{
    public required string Label { get; init; }
    public int Today { get; init; }
    public int Week { get; init; }
    public int Month { get; init; }
    public int Total { get; init; }
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

        Rows.Clear();
        AddRow("Reviews completed", StatMetric.ReviewsCompleted, day, week, month, total);
        AddRow("PRs opened", StatMetric.OwnPrsOpened, day, week, month, total);
        AddRow("PRs merged", StatMetric.OwnPrsMerged, day, week, month, total);
        AddRow("CI failures", StatMetric.CiFailures, day, week, month, total);
        AddRow("Flaky reruns", StatMetric.FlakyReruns, day, week, month, total);
        AddRow("Real failures", StatMetric.RealFailures, day, week, month, total);

        LastUpdated = DateTime.Now.ToString("HH:mm:ss");
    }

    private void AddRow(string label, StatMetric metric, DayStat day, DayStat week, DayStat month, DayStat total)
    {
        Rows.Add(new StatRowViewModel
        {
            Label = label,
            Today = day.Get(metric),
            Week = week.Get(metric),
            Month = month.Get(metric),
            Total = total.Get(metric),
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
