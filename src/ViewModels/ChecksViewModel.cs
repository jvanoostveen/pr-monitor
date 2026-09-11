using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using PrMonitor.Models;
using PrMonitor.Services;

namespace PrMonitor.ViewModels;

/// <summary>
/// Backs the CI checks panel that overlays the PR list. Opened from a PR row's status dot,
/// it loads the checks of that PR's head commit on demand — polling never fetches them,
/// because one extra GraphQL call per PR per poll would dwarf the poll itself.
/// </summary>
public sealed class ChecksViewModel : INotifyPropertyChanged
{
    private readonly GitHubService _gitHub;
    private readonly DiagnosticsLogger _logger;

    /// <summary>Guards against a slow response for a previously opened PR overwriting the current one.</summary>
    private int _loadGeneration;

    /// <summary>
    /// Normal wait before reloading while at least one job is still running, and the floor for
    /// the budget-aware pacing below.
    /// </summary>
    internal static readonly TimeSpan AutoRefreshInterval = TimeSpan.FromSeconds(30);

    /// <summary>Slowest the panel will ever poll before giving up on automatic reloads.</summary>
    internal static readonly TimeSpan MaxAutoRefreshInterval = TimeSpan.FromMinutes(5);

    /// <summary>
    /// GraphQL points that must be left before another automatic reload is scheduled. The panel
    /// backs off well before GitHub starts refusing calls, so manual actions keep working.
    /// </summary>
    internal const int MinRateLimitRemaining = 100;

    /// <summary>
    /// Share of the remaining budget the panel may spend before the window resets. Watching one
    /// PR must never be able to starve polling, notifications and manual actions.
    /// </summary>
    internal const double BudgetShare = 0.2;

    /// <summary>
    /// Holds the pending reload. It is scheduled only while the panel is open and a job is
    /// still running, and never holds more than one tick.
    /// </summary>
    private readonly AutoRefreshScheduler _autoRefresh;

    public ChecksViewModel(GitHubService gitHub, DiagnosticsLogger logger)
    {
        _gitHub = gitHub;
        _logger = logger;
        _autoRefresh = new AutoRefreshScheduler(RefreshAsync, logger);
    }

    public ObservableCollection<CheckItemViewModel> Checks { get; } = [];

    /// <summary>The PR the panel is currently showing, used by refresh and the footer button.</summary>
    public PrItemViewModel? CurrentPr { get; private set; }

    private bool _isOpen;
    /// <summary>Whether the overlay is shown.</summary>
    public bool IsOpen
    {
        get => _isOpen;
        private set => SetField(ref _isOpen, value);
    }

    private bool _isLoading;
    public bool IsLoading
    {
        get => _isLoading;
        private set
        {
            SetField(ref _isLoading, value);
            NotifyStateFlags();
        }
    }

    private string _title = "";
    public string Title
    {
        get => _title;
        private set => SetField(ref _title, value);
    }

    private string _repository = "";
    public string Repository
    {
        get => _repository;
        private set => SetField(ref _repository, value);
    }

    private string _branchName = "";
    /// <summary>Head branch of the PR, shown next to the repository.</summary>
    public string BranchName
    {
        get => _branchName;
        private set => SetField(ref _branchName, value);
    }

    private string _prNumberText = "";
    public string PrNumberText
    {
        get => _prNumberText;
        private set => SetField(ref _prNumberText, value);
    }

    private string _author = "";
    public string Author
    {
        get => _author;
        private set => SetField(ref _author, value);
    }

    private string _timeAgo = "";
    public string TimeAgo
    {
        get => _timeAgo;
        private set => SetField(ref _timeAgo, value);
    }

    private string _summaryLabel = "";
    /// <summary>Headline over the job list, e.g. "1 CHECK FAILED" or "CHECKS RUNNING".</summary>
    public string SummaryLabel
    {
        get => _summaryLabel;
        private set => SetField(ref _summaryLabel, value);
    }

    private string _summaryCount = "";
    /// <summary>"3/4" — finished checks over total, skipped checks excluded.</summary>
    public string SummaryCount
    {
        get => _summaryCount;
        private set => SetField(ref _summaryCount, value);
    }

    private CIState _summaryState = CIState.Unknown;
    /// <summary>Drives the accent colour of the summary line, reusing the PR row's CI brush.</summary>
    public CIState SummaryState
    {
        get => _summaryState;
        private set => SetField(ref _summaryState, value);
    }

    private string _errorMessage = "";
    public string ErrorMessage
    {
        get => _errorMessage;
        private set
        {
            SetField(ref _errorMessage, value);
            OnPropertyChanged(nameof(HasError));
            NotifyStateFlags();
        }
    }

    public bool HasError => !string.IsNullOrEmpty(ErrorMessage);

    private string _noticeMessage = "";
    /// <summary>Non-fatal message shown under a list that is still usable (e.g. a failed reload).</summary>
    public string NoticeMessage
    {
        get => _noticeMessage;
        private set
        {
            SetField(ref _noticeMessage, value);
            OnPropertyChanged(nameof(HasNotice));
        }
    }

    public bool HasNotice => !string.IsNullOrEmpty(NoticeMessage);

    private bool _isAutoRefreshing;
    /// <summary>True while a reload is scheduled, i.e. the panel is open and a job is still running.</summary>
    public bool IsAutoRefreshing
    {
        get => _isAutoRefreshing;
        private set
        {
            SetField(ref _isAutoRefreshing, value);
            NotifyAutoRefreshIndicator();
        }
    }

    private TimeSpan _currentInterval = AutoRefreshInterval;
    /// <summary>Interval the next reload was actually scheduled at; may exceed the base when throttled.</summary>
    public TimeSpan CurrentInterval
    {
        get => _currentInterval;
        private set
        {
            SetField(ref _currentInterval, value);
            NotifyAutoRefreshIndicator();
        }
    }

    // ── Refresh button state (mirrors the header's pin button: glyph + colour + tooltip) ──

    /// <summary>Material Symbols glyph: "autorenew" while self-updating, plain "refresh" otherwise.</summary>
    public string RefreshIcon => IsAutoRefreshing ? "" : "";

    /// <summary>Short caption next to the icon, so the state is readable without hovering.</summary>
    public string AutoRefreshLabel => IsAutoRefreshing ? FormatInterval(CurrentInterval) : "";

    /// <summary>Whether to show the caption at all.</summary>
    public bool ShowAutoRefreshLabel => IsAutoRefreshing;

    /// <summary>Tooltip of the refresh button; says whether the panel updates itself, and why not.</summary>
    public string RefreshTooltip => IsAutoRefreshing
        ? $"Auto-refresh is on — reloading every {FormatInterval(CurrentInterval)} while jobs are running.\nClick to reload now."
        : "Auto-refresh is off — no jobs are running.\nClick to reload.";

    private void NotifyAutoRefreshIndicator()
    {
        OnPropertyChanged(nameof(RefreshIcon));
        OnPropertyChanged(nameof(RefreshTooltip));
        OnPropertyChanged(nameof(AutoRefreshLabel));
        OnPropertyChanged(nameof(ShowAutoRefreshLabel));
    }

    /// <summary>Compact interval caption: "30s", "2m", "1m 30s".</summary>
    internal static string FormatInterval(TimeSpan interval)
    {
        if (interval.TotalSeconds < 60) return $"{interval.TotalSeconds:0}s";
        int minutes = (int)interval.TotalMinutes;
        int seconds = interval.Seconds;
        return seconds == 0 ? $"{minutes}m" : $"{minutes}m {seconds}s";
    }

    /// <summary>Shown when loading finished and GitHub reported no checks at all.</summary>
    public bool ShowEmptyState => !IsLoading && !HasError && _allRows.Count == 0;

    /// <summary>Shown when at least one check row is available.</summary>
    public bool ShowList => !IsLoading && Checks.Count > 0;

    // ── Skipped checks ──────────────────────────────────────────────────

    /// <summary>Every collapsed row of the last load, including the skipped ones the list hides.</summary>
    private List<(CheckRunInfo Check, int Count)> _allRows = [];

    private bool _showSkipped;
    /// <summary>
    /// Whether skipped checks are listed. Off by default: a job that never ran says nothing
    /// about the PR, and a workflow re-triggered per review can contribute a dozen of them,
    /// crowding out the checks that do matter. Kept for the lifetime of the app, not reset by
    /// closing the panel, so someone who wants to see them does not have to ask twice.
    /// </summary>
    public bool ShowSkipped
    {
        get => _showSkipped;
        private set
        {
            SetField(ref _showSkipped, value);
            OnPropertyChanged(nameof(SkippedToggleText));
        }
    }

    /// <summary>Distinct skipped jobs in the current load, regardless of how often each repeated.</summary>
    public int SkippedRowCount => _allRows.Count(r => r.Check.IsSkipped);

    public bool HasSkipped => SkippedRowCount > 0;

    /// <summary>Caption of the line that reveals or hides the skipped checks.</summary>
    public string SkippedToggleText => BuildSkippedToggleText(SkippedRowCount, ShowSkipped);

    /// <summary>"Show 3 skipped checks" / "Hide 1 skipped check".</summary>
    internal static string BuildSkippedToggleText(int count, bool showSkipped)
    {
        var noun = count == 1 ? "skipped check" : "skipped checks";
        return showSkipped ? $"Hide {count} {noun}" : $"Show {count} {noun}";
    }

    /// <summary>
    /// The rows the list actually renders. Skipped checks are filtered out unless the user
    /// asked for them — they never ran, so they carry no signal about the PR.
    /// </summary>
    internal static List<(CheckRunInfo Check, int Count)> VisibleRows(
        IReadOnlyList<(CheckRunInfo Check, int Count)> rows, bool showSkipped) =>
        [.. rows.Where(r => showSkipped || !r.Check.IsSkipped)];

    /// <summary>Flips the skipped checks into or out of the list without re-fetching anything.</summary>
    public void ToggleSkipped()
    {
        ShowSkipped = !ShowSkipped;
        RebuildVisibleRows();
    }

    /// <summary>Refills the bound collection from <see cref="_allRows"/>, honouring the filter.</summary>
    private void RebuildVisibleRows()
    {
        Checks.Clear();
        foreach (var (check, count) in VisibleRows(_allRows, ShowSkipped))
            Checks.Add(new CheckItemViewModel(check, count));

        OnPropertyChanged(nameof(SkippedRowCount));
        OnPropertyChanged(nameof(HasSkipped));
        OnPropertyChanged(nameof(SkippedToggleText));
        NotifyStateFlags();
    }

    /// <summary>
    /// Opens the panel for a PR and starts loading its checks. Header fields come from the row
    /// immediately, so the panel never renders blank while the API call is in flight.
    /// </summary>
    public async Task OpenAsync(PrItemViewModel pr)
    {
        // Opening another PR must not leave the previous one's reload pending.
        CancelAutoRefresh();

        CurrentPr = pr;
        Title = pr.Title;
        Repository = pr.Repository;
        BranchName = pr.HeadRefName;
        PrNumberText = $"#{pr.Number}";
        Author = pr.Author;
        TimeAgo = pr.TimeAgo;
        SummaryState = pr.EffectiveCIState;
        SummaryLabel = "LOADING CHECKS";
        SummaryCount = "";
        ErrorMessage = "";
        NoticeMessage = "";
        _allRows = [];
        Checks.Clear();
        IsOpen = true;

        await RefreshAsync();
    }

    /// <summary>
    /// Re-fetches the checks of the PR the panel is currently showing. Used by the refresh
    /// button and by the auto-refresh tick; either way any pending tick is cancelled first,
    /// so a manual refresh cannot leave two timers running.
    /// </summary>
    public async Task RefreshAsync()
    {
        CancelAutoRefresh();

        if (CurrentPr is not { } pr)
            return;

        int generation = ++_loadGeneration;
        // Keep an earlier successful list on screen while reloading, so an auto-refresh does
        // not blank the panel every 30 seconds.
        IsLoading = Checks.Count == 0;

        var (owner, repo) = SplitRepository(pr.Repository);
        if (owner is null || repo is null)
        {
            Finish(generation, CheckFetchResult.Failure(), "Could not determine the repository of this PR.");
            return;
        }

        try
        {
            var result = await _gitHub.FetchPrChecksAsync(owner, repo, pr.Number);
            Finish(generation, result, DescribeFailure(result));
        }
        catch (Exception ex)
        {
            _logger.Warn($"ChecksViewModel: loading checks for {pr.Repository}#{pr.Number} failed: {ex.Message}");
            Finish(generation, CheckFetchResult.Failure(), "Could not load the checks. Is 'gh' still authenticated?");
        }
    }

    /// <summary>User-facing text for a failed fetch, or empty when the fetch succeeded.</summary>
    internal static string DescribeFailure(CheckFetchResult result) => result.Status switch
    {
        CheckFetchStatus.RateLimited => result.RateLimitResetAt is { } reset
            ? $"GitHub rate limit reached — auto-refresh stopped until {reset.ToLocalTime():HH:mm}."
            : "GitHub rate limit reached — auto-refresh stopped.",
        CheckFetchStatus.Failed => "Could not load the checks. Is 'gh' still authenticated?",
        _ => "",
    };

    private void Finish(int generation, CheckFetchResult result, string error)
    {
        // A newer open/refresh already took over — drop this stale response.
        if (generation != _loadGeneration)
            return;

        // A failed reload keeps the rows it already had: a momentary API hiccup should not
        // throw away a list the user is reading. The message then shows below it instead.
        bool keepExistingRows = result.IsFailure && _allRows.Count > 0;
        if (!keepExistingRows)
        {
            _allRows = Collapse(result.Checks);
            RebuildVisibleRows();

            // The summary counts collapsed rows, so a PR with nine identical skipped runs is
            // not reported as nine checks.
            UpdateSummary([.. _allRows.Select(r => r.Check)]);
        }

        NoticeMessage = keepExistingRows ? error : "";
        ErrorMessage = keepExistingRows ? "" : error;
        IsLoading = false;

        ScheduleAutoRefresh(result);
    }

    // ── Auto-refresh ────────────────────────────────────────────────────

    /// <summary>
    /// Whether another automatic reload may be scheduled. It requires a healthy response with
    /// at least one job still running, and enough rate-limit budget left — so a finished PR,
    /// a failed call or a throttled account all simply stop the cycle.
    /// </summary>
    internal static bool ShouldAutoRefresh(CheckFetchResult result, int? remaining) =>
        result.Status == CheckFetchStatus.Ok
        && result.Checks.Any(c => c.IsInProgress)
        && (remaining ?? int.MaxValue) >= MinRateLimitRemaining;

    /// <summary>
    /// How long to wait before the next reload, given what is left of the rate-limit budget.
    /// </summary>
    /// <remarks>
    /// With a healthy budget this is simply <see cref="AutoRefreshInterval"/>. When the budget
    /// runs thin the panel spreads the share it is allowed to spend (<see cref="BudgetShare"/>)
    /// evenly over the time left in the window, rather than keeping a fixed pace until GitHub
    /// cuts it off. Nothing to go on means the normal interval: an unknown budget is not a
    /// reason to crawl.
    /// </remarks>
    internal static TimeSpan ComputeInterval(int? remaining, DateTimeOffset? resetAt, DateTimeOffset now)
    {
        if (remaining is not { } left || resetAt is not { } reset)
            return AutoRefreshInterval;

        var untilReset = reset - now;
        // The window is about to roll over; the budget replenishes before pacing could matter.
        if (untilReset <= TimeSpan.Zero)
            return AutoRefreshInterval;

        double affordableCalls = left * BudgetShare;
        if (affordableCalls < 1)
            return MaxAutoRefreshInterval;

        var paced = TimeSpan.FromSeconds(untilReset.TotalSeconds / affordableCalls);
        return paced < AutoRefreshInterval ? AutoRefreshInterval
            : paced > MaxAutoRefreshInterval ? MaxAutoRefreshInterval
            : paced;
    }

    /// <summary>
    /// Budget to pace against: what this call reported, falling back to what the last poll saw.
    /// Polling asks for <c>rateLimit</c> on every query, so the fallback is usually seconds old.
    /// </summary>
    private (int? Remaining, DateTimeOffset? ResetAt) EffectiveBudget(CheckFetchResult result)
    {
        if (result.RateLimitRemaining is not null)
            return (result.RateLimitRemaining, result.RateLimitResetAt);

        var polled = _gitHub.LastRateLimit;
        return polled is null || polled.IsStale(DateTimeOffset.UtcNow)
            ? (null, null)
            : (polled.Remaining, polled.ResetAt);
    }

    /// <summary>
    /// Schedules the next reload when <see cref="ShouldAutoRefresh"/> allows it. Each tick is a
    /// single delay that re-decides afterwards; there is no repeating timer that could outlive
    /// the panel or keep firing against a rate-limited API.
    /// </summary>
    private void ScheduleAutoRefresh(CheckFetchResult result)
    {
        var (remaining, resetAt) = EffectiveBudget(result);

        if (!IsOpen || !ShouldAutoRefresh(result, remaining))
        {
            CancelAutoRefresh();
            return;
        }

        var interval = ComputeInterval(remaining, resetAt, DateTimeOffset.UtcNow);
        if (interval > AutoRefreshInterval)
            _logger.Info($"ChecksViewModel: slowing auto-refresh to {interval.TotalSeconds:0} s — {remaining} GraphQL points left.");

        CurrentInterval = interval;
        _autoRefresh.Schedule(interval);
        IsAutoRefreshing = true;
    }

    /// <summary>Stops a pending auto-refresh, if any. Safe to call when none is scheduled.</summary>
    private void CancelAutoRefresh()
    {
        _autoRefresh.Cancel();
        IsAutoRefreshing = false;
    }

    /// <summary>Test hook: whether a reload is actually pending in the scheduler.</summary>
    internal bool HasPendingAutoRefresh => _autoRefresh.IsScheduled;

    /// <summary>
    /// Groups the raw checks into the rows the panel shows: one row per
    /// (workflow, job, state), newest run first, ordered by what needs attention.
    /// </summary>
    /// <remarks>
    /// GitHub returns one check run per check suite, and a workflow triggered by
    /// <c>pull_request_review</c> gets a fresh suite on every review — so a PR can carry nine
    /// identical skipped "Claude Code / claude" runs. Collapsing runs that agree on workflow,
    /// name *and* state throws away no information beyond the repeat count, which the row shows.
    /// Runs of the same job in different states are deliberately kept apart: a job that failed
    /// and passed on a rerun is something the user has to see, not something to hide.
    /// </remarks>
    internal static List<(CheckRunInfo Check, int Count)> Collapse(IReadOnlyList<CheckRunInfo> checks) =>
    [
        .. checks
            .GroupBy(c => (c.WorkflowName, c.Name, c.State))
            .Select(g => (
                Check: g.OrderByDescending(c => c.WorkflowRunId)
                        .ThenByDescending(c => c.StartedAt ?? DateTimeOffset.MinValue)
                        .First(),
                Count: g.Count()))
            .OrderBy(r => r.Check.SortRank)
            .ThenBy(r => r.Check.WorkflowName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(r => r.Check.Name, StringComparer.OrdinalIgnoreCase)
    ];

    private void UpdateSummary(IReadOnlyList<CheckRunInfo> checks)
    {
        var (label, state, count) = BuildSummary(checks);
        SummaryLabel = label;
        SummaryState = state;
        SummaryCount = count;
    }

    /// <summary>
    /// Summary headline, accent state and "done/total" counter. Skipped checks are excluded from
    /// the counter: they never run, so counting them makes a finished PR look unfinished.
    /// </summary>
    internal static (string Label, CIState State, string Count) BuildSummary(IReadOnlyList<CheckRunInfo> checks)
    {
        if (checks.Count == 0)
            return ("NO CHECKS", CIState.Unknown, "");

        var relevant = checks.Where(c => !c.IsSkipped).ToList();
        int total = relevant.Count;
        int done = relevant.Count(c => !c.IsInProgress);
        string count = total == 0 ? "" : $"{done}/{total}";

        int failed = checks.Count(c => c.IsFailure);
        if (failed > 0)
            return (failed == 1 ? "1 CHECK FAILED" : $"{failed} CHECKS FAILED", CIState.Failure, count);

        if (checks.Any(c => c.IsInProgress))
            return ("CHECKS RUNNING", CIState.Pending, count);

        if (total > 0 && relevant.All(c => c.State == CheckRunState.Success))
            return ("ALL CHECKS PASSED", CIState.Success, count);

        return ("CHECKS COMPLETED", CIState.Unknown, count);
    }

    /// <summary>Closes the overlay, stops the auto-refresh and releases the loaded rows.</summary>
    public void Close()
    {
        CancelAutoRefresh();
        _loadGeneration++;
        IsOpen = false;
        CurrentPr = null;
        _allRows = [];
        Checks.Clear();
        ErrorMessage = "";
        NoticeMessage = "";
        IsLoading = false;
    }

    /// <summary>Opens the PR itself on GitHub (footer button).</summary>
    public void OpenPrInBrowser() => CurrentPr?.OpenInBrowser();

    /// <summary>Splits "owner/repo" into its parts; returns nulls when the format is unexpected.</summary>
    internal static (string? Owner, string? Repo) SplitRepository(string repository)
    {
        var parts = repository.Split('/');
        return parts.Length == 2 && parts[0].Length > 0 && parts[1].Length > 0
            ? (parts[0], parts[1])
            : (null, null);
    }

    private void NotifyStateFlags()
    {
        OnPropertyChanged(nameof(ShowEmptyState));
        OnPropertyChanged(nameof(ShowList));
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void SetField<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        OnPropertyChanged(name);
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>One job row in the CI checks panel.</summary>
public sealed class CheckItemViewModel
{
    private readonly CheckRunInfo _check;

    public CheckItemViewModel(CheckRunInfo check, int duplicateCount = 1)
    {
        _check = check;
        DuplicateCount = duplicateCount;
    }

    public string Name => _check.Name;

    public string WorkflowName => _check.WorkflowName;

    /// <summary>How many identical runs this row stands for; 1 for an ordinary row.</summary>
    public int DuplicateCount { get; }

    /// <summary>
    /// Whether this row has a workflow to show in front of the job name. False for legacy
    /// status contexts, which belong to no workflow.
    /// </summary>
    public bool HasWorkflow => !string.IsNullOrEmpty(WorkflowName);

    /// <summary>
    /// Workflow label rendered dimmed before the job name, the way GitHub writes
    /// "Components / Test". The " / " separator is a separate element, so a long workflow name
    /// can trim to an ellipsis without swallowing the separator or the job name.
    /// </summary>
    public string WorkflowLabel => WorkflowName;

    /// <summary>"×9" when several identical runs were collapsed into this row, otherwise empty.</summary>
    public string DuplicateBadge => DuplicateCount > 1 ? $"×{DuplicateCount}" : "";

    public bool HasDuplicates => DuplicateCount > 1;

    public CheckRunState State => _check.State;

    public string Url => _check.Url;

    /// <summary>Material Symbols glyph for the state icon.</summary>
    public string StateIcon => State switch
    {
        CheckRunState.Success => "",    // check
        CheckRunState.Failure => "",    // close
        CheckRunState.Cancelled => "",  // block
        CheckRunState.Running => "",    // pending
        CheckRunState.Queued => "",     // schedule
        CheckRunState.Skipped => "",    // remove
        CheckRunState.Neutral => "",    // info
        _ => "",                        // help
    };

    /// <summary>Right-hand column: elapsed/total time, or the state when there is no duration.</summary>
    public string DurationText => State switch
    {
        CheckRunState.Skipped => "skipped",
        CheckRunState.Queued => "queued",
        CheckRunState.Running => _check.Duration is { } running ? $"{FormatDuration(running)}…" : "running…",
        _ => _check.Duration is { } done ? FormatDuration(done) : "",
    };

    /// <summary>Whether this row links to a job log on GitHub.</summary>
    public bool HasUrl => !string.IsNullOrWhiteSpace(Url);

    /// <summary>GitHub Actions job id, for rerunning this job alone.</summary>
    public long JobId => _check.JobId;

    /// <summary>
    /// Whether the rerun button applies: a failed GitHub Actions job. Passing jobs are not
    /// offered a rerun, and status contexts have nothing to rerun.
    /// </summary>
    public bool CanRerun => _check.CanRerun;

    public string RerunTooltip => $"Rerun this job ({Name})";

    public string RowTooltip
    {
        get
        {
            var lines = new List<string>();
            if (!string.IsNullOrEmpty(WorkflowName))
                lines.Add($"Workflow: {WorkflowName}");
            lines.Add($"Job: {Name}");
            if (!string.IsNullOrEmpty(_check.Event))
                lines.Add($"Triggered by: {_check.Event}");
            lines.Add($"Status: {State}");
            if (HasDuplicates)
                lines.Add($"{DuplicateCount} identical runs — showing the most recent");
            if (HasUrl)
                lines.Add("Click to open the job log");
            return string.Join(Environment.NewLine, lines);
        }
    }

    /// <summary>Opens the job's log page on GitHub.</summary>
    public void OpenInBrowser()
    {
        if (Uri.TryCreate(Url, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps)
            Process.Start(new ProcessStartInfo(Url) { UseShellExecute = true });
    }

    /// <summary>Compact duration: "22s", "1m 24s", "1h 05m".</summary>
    internal static string FormatDuration(TimeSpan span)
    {
        if (span < TimeSpan.Zero) span = TimeSpan.Zero;
        if (span.TotalMinutes < 1) return $"{(int)span.TotalSeconds}s";
        if (span.TotalHours < 1) return $"{(int)span.TotalMinutes}m {span.Seconds:00}s";
        return $"{(int)span.TotalHours}h {span.Minutes:00}m";
    }
}
