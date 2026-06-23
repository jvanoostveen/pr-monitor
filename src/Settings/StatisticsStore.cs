using System.Globalization;
using System.IO;
using System.Threading;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PrMonitor.Settings;

/// <summary>
/// The activity metrics tracked over time. Each value is counted while the app is running.
/// </summary>
public enum StatMetric
{
    ReviewsRequested,
    ReviewsCompleted,
    OwnPrsOpened,
    OwnPrsMerged,
    CiFailures,
    FlakyReruns,
    RealFailures,
}

/// <summary>
/// Aggregated counters for a single day (or a summed range / total).
/// </summary>
public sealed class DayStat
{
    public int ReviewsRequested { get; set; }
    public int ReviewsCompleted { get; set; }
    public int OwnPrsOpened { get; set; }

    /// <summary>Per-author breakdown for the ReviewsRequested counter.</summary>
    public Dictionary<string, int>? ReviewsRequestedByAuthor { get; set; }
    public int OwnPrsMerged { get; set; }
    public int CiFailures { get; set; }
    public int FlakyReruns { get; set; }
    public int RealFailures { get; set; }

    public int Get(StatMetric metric) => metric switch
    {
        StatMetric.ReviewsRequested => ReviewsRequested,
        StatMetric.ReviewsCompleted => ReviewsCompleted,
        StatMetric.OwnPrsOpened => OwnPrsOpened,
        StatMetric.OwnPrsMerged => OwnPrsMerged,
        StatMetric.CiFailures => CiFailures,
        StatMetric.FlakyReruns => FlakyReruns,
        StatMetric.RealFailures => RealFailures,
        _ => 0,
    };

    public void Add(StatMetric metric, int delta)
    {
        switch (metric)
        {
            case StatMetric.ReviewsRequested: ReviewsRequested += delta; break;
            case StatMetric.ReviewsCompleted: ReviewsCompleted += delta; break;
            case StatMetric.OwnPrsOpened: OwnPrsOpened += delta; break;
            case StatMetric.OwnPrsMerged: OwnPrsMerged += delta; break;
            case StatMetric.CiFailures: CiFailures += delta; break;
            case StatMetric.FlakyReruns: FlakyReruns += delta; break;
            case StatMetric.RealFailures: RealFailures += delta; break;
        }
    }

    internal void Accumulate(DayStat other)
    {
        ReviewsRequested += other.ReviewsRequested;
        ReviewsCompleted += other.ReviewsCompleted;
        OwnPrsOpened += other.OwnPrsOpened;
        OwnPrsMerged += other.OwnPrsMerged;
        CiFailures += other.CiFailures;
        FlakyReruns += other.FlakyReruns;
        RealFailures += other.RealFailures;

        if (other.ReviewsRequestedByAuthor is { } otherAuthors)
        {
            ReviewsRequestedByAuthor ??= new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var (author, count) in otherAuthors)
                ReviewsRequestedByAuthor[author] = ReviewsRequestedByAuthor.GetValueOrDefault(author) + count;
        }
    }
}

/// <summary>
/// Time-series store of PR activity statistics, persisted to
/// <c>%APPDATA%/pr-monitor/statistics.json</c> as daily buckets.
/// Mirrors <see cref="AppSettings"/>'s load/save pattern (atomic write with a
/// best-effort <c>.bak</c> backup and a test-only path override) so running the
/// test suite never touches the real statistics file.
/// </summary>
public sealed class StatisticsStore
{
    private const string DateKeyFormat = "yyyy-MM-dd";

    /// <summary>Daily buckets older than this are pruned on load (~18 months).</summary>
    private const int RetentionDays = 550;

    private static readonly string StatsDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "pr-monitor");

    private static readonly string StatsPath =
        Path.Combine(StatsDir, "statistics.json");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private static readonly AsyncLocal<string?> StatsPathOverride = new();

    /// <summary>
    /// Path this store was loaded from. <see cref="Save"/> writes back here so a
    /// service holding a store instance can never overwrite the production file
    /// during tests, even outside an <see cref="UseStatisticsPathOverride"/> scope.
    /// </summary>
    [JsonIgnore]
    private string? _sourcePath;

    /// <summary>Per-day counters keyed by date string (<c>yyyy-MM-dd</c>).</summary>
    public Dictionary<string, DayStat> Days { get; set; } = new();

    /// <summary>
    /// Load statistics from disk, or return an empty store if no file exists.
    /// </summary>
    public static StatisticsStore Load() => LoadFrom(GetStatsPath());

    /// <summary>
    /// Test helper: temporarily override the default statistics path for the current async flow.
    /// </summary>
    internal static IDisposable UseStatisticsPathOverride(string path)
    {
        var previousPath = StatsPathOverride.Value;
        StatsPathOverride.Value = path;
        return new ActionOnDispose(() => StatsPathOverride.Value = previousPath);
    }

    internal static StatisticsStore LoadFrom(string path)
    {
        var store = TryDeserialize(path);

        // Resilience: if the primary file is corrupted/partial, recover from last known good backup.
        store ??= TryDeserialize(path + ".bak");
        store ??= new StatisticsStore();

        store.Days ??= new Dictionary<string, DayStat>();
        store._sourcePath = path;

        // Prune buckets older than the retention window.
        var cutoff = DateOnly.FromDateTime(DateTime.Today).AddDays(-RetentionDays);
        foreach (var key in store.Days.Keys.ToList())
        {
            if (!DateOnly.TryParseExact(key, DateKeyFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var day)
                || day < cutoff)
            {
                store.Days.Remove(key);
            }
        }

        return store;
    }

    /// <summary>
    /// Persist current statistics to disk (atomic write with a <c>.bak</c> backup).
    /// </summary>
    public void Save() => SaveTo(_sourcePath ?? GetStatsPath());

    /// <summary>
    /// Clear all collected statistics and persist the empty store to disk.
    /// </summary>
    public void Reset()
    {
        Days.Clear();
        Save();
    }

    internal void SaveTo(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var json = JsonSerializer.Serialize(this, JsonOptions);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, json);

        // Keep a best-effort backup of the previous good file.
        if (File.Exists(path))
            File.Copy(path, path + ".bak", overwrite: true);

        File.Move(tmp, path, overwrite: true);
    }

    // ── Mutation ────────────────────────────────────────────────────────

    /// <summary>
    /// Increment <see cref="StatMetric.ReviewsRequested"/> for today, also tracking the PR author.
    /// </summary>
    public void IncrementReviewRequested(DateOnly day, string author)
    {
        var key = day.ToString(DateKeyFormat, CultureInfo.InvariantCulture);
        if (!Days.TryGetValue(key, out var stat))
        {
            stat = new DayStat();
            Days[key] = stat;
        }
        stat.ReviewsRequested++;
        stat.ReviewsRequestedByAuthor ??= new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        stat.ReviewsRequestedByAuthor[author] =
            stat.ReviewsRequestedByAuthor.GetValueOrDefault(author) + 1;
    }

    /// <summary>Increment a metric for today.</summary>
    public void Increment(StatMetric metric, int delta = 1) =>
        Increment(DateOnly.FromDateTime(DateTime.Today), metric, delta);

    /// <summary>Increment a metric for a specific day.</summary>
    public void Increment(DateOnly day, StatMetric metric, int delta = 1)
    {
        var key = day.ToString(DateKeyFormat, CultureInfo.InvariantCulture);
        if (!Days.TryGetValue(key, out var stat))
        {
            stat = new DayStat();
            Days[key] = stat;
        }
        stat.Add(metric, delta);
    }

    // ── Aggregation ─────────────────────────────────────────────────────

    /// <summary>Counters for a single day.</summary>
    public DayStat ForDay(DateOnly day)
    {
        var key = day.ToString(DateKeyFormat, CultureInfo.InvariantCulture);
        var result = new DayStat();
        if (Days.TryGetValue(key, out var stat))
            result.Accumulate(stat);
        return result;
    }

    /// <summary>Counters summed across the ISO week (Monday–Sunday) containing <paramref name="day"/>.</summary>
    public DayStat ForWeekOf(DateOnly day)
    {
        var mondayOffset = ((int)day.DayOfWeek + 6) % 7; // Monday = 0
        var start = day.AddDays(-mondayOffset);
        return ForRange(start, start.AddDays(6));
    }

    /// <summary>Counters summed across the calendar month containing <paramref name="day"/>.</summary>
    public DayStat ForMonthOf(DateOnly day)
    {
        var start = new DateOnly(day.Year, day.Month, 1);
        return ForRange(start, start.AddMonths(1).AddDays(-1));
    }

    /// <summary>Counters summed across an inclusive day range.</summary>
    public DayStat ForRange(DateOnly start, DateOnly endInclusive)
    {
        var result = new DayStat();
        foreach (var (key, stat) in Days)
        {
            if (DateOnly.TryParseExact(key, DateKeyFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var day)
                && day >= start && day <= endInclusive)
            {
                result.Accumulate(stat);
            }
        }
        return result;
    }

    /// <summary>Counters summed across all recorded days.</summary>
    public DayStat Total()
    {
        var result = new DayStat();
        foreach (var stat in Days.Values)
            result.Accumulate(stat);
        return result;
    }

    // ── Internals ───────────────────────────────────────────────────────

    private static StatisticsStore? TryDeserialize(string path)
    {
        if (!File.Exists(path))
            return null;

        try
        {
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<StatisticsStore>(json, JsonOptions);
        }
        catch
        {
            return null;
        }
    }

    private static string GetStatsPath() => StatsPathOverride.Value ?? StatsPath;

    private sealed class ActionOnDispose(Action onDispose) : IDisposable
    {
        private Action? _onDispose = onDispose;

        public void Dispose()
        {
            _onDispose?.Invoke();
            _onDispose = null;
        }
    }
}
