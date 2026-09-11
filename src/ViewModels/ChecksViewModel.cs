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

    public ChecksViewModel(GitHubService gitHub, DiagnosticsLogger logger)
    {
        _gitHub = gitHub;
        _logger = logger;
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

    /// <summary>Shown when loading finished and GitHub reported no checks at all.</summary>
    public bool ShowEmptyState => !IsLoading && !HasError && Checks.Count == 0;

    /// <summary>Shown when at least one check row is available.</summary>
    public bool ShowList => !IsLoading && Checks.Count > 0;

    /// <summary>
    /// Opens the panel for a PR and starts loading its checks. Header fields come from the row
    /// immediately, so the panel never renders blank while the API call is in flight.
    /// </summary>
    public async Task OpenAsync(PrItemViewModel pr)
    {
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
        Checks.Clear();
        IsOpen = true;

        await RefreshAsync();
    }

    /// <summary>Re-fetches the checks of the PR the panel is currently showing.</summary>
    public async Task RefreshAsync()
    {
        if (CurrentPr is not { } pr)
            return;

        int generation = ++_loadGeneration;
        ErrorMessage = "";
        IsLoading = true;

        var (owner, repo) = SplitRepository(pr.Repository);
        if (owner is null || repo is null)
        {
            Finish(generation, [], "Could not determine the repository of this PR.");
            return;
        }

        try
        {
            var checks = await _gitHub.FetchPrChecksAsync(owner, repo, pr.Number);
            Finish(generation, checks, "");
        }
        catch (Exception ex)
        {
            _logger.Warn($"ChecksViewModel: loading checks for {pr.Repository}#{pr.Number} failed: {ex.Message}");
            Finish(generation, [], "Could not load the checks. Is 'gh' still authenticated?");
        }
    }

    private void Finish(int generation, IReadOnlyList<CheckRunInfo> checks, string error)
    {
        // A newer open/refresh already took over — drop this stale response.
        if (generation != _loadGeneration)
            return;

        Checks.Clear();
        foreach (var check in checks.OrderBy(c => c.SortRank).ThenBy(c => c.Name, StringComparer.OrdinalIgnoreCase))
            Checks.Add(new CheckItemViewModel(check));

        UpdateSummary(checks);
        ErrorMessage = error;
        IsLoading = false;
    }

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

    /// <summary>Closes the overlay and releases the loaded rows.</summary>
    public void Close()
    {
        _loadGeneration++;
        IsOpen = false;
        CurrentPr = null;
        Checks.Clear();
        ErrorMessage = "";
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

    public CheckItemViewModel(CheckRunInfo check) => _check = check;

    public string Name => _check.Name;

    public string WorkflowName => _check.WorkflowName;

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

    public string RowTooltip => string.IsNullOrEmpty(WorkflowName)
        ? $"{Name} — {State}{(HasUrl ? "\nClick to open the job log" : "")}"
        : $"{WorkflowName} › {Name} — {State}{(HasUrl ? "\nClick to open the job log" : "")}";

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
