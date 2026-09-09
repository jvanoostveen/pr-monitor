using PrMonitor.Models;
using PrMonitor.Settings;

namespace PrMonitor.Services;

/// <summary>
/// Event args carrying information about a change detected between polls.
/// </summary>
public sealed class PrChangeEventArgs : EventArgs
{
    public required PullRequestInfo PullRequest { get; init; }
    public required PrChangeKind Kind { get; init; }
    /// <summary>Only set for <see cref="PrChangeKind.CIStatusChanged"/>.</summary>
    public CIState PreviousCIState { get; init; }
}

public enum PrChangeKind
{
    NewAutoMergePr,
    RemovedAutoMergePr,
    CIStatusChanged,
    NewReviewRequested,
    ReviewRequestRemoved,
}

/// <summary>
/// Snapshot of the latest poll results plus aggregate counts.
/// </summary>
public sealed class PollSnapshot
{
    public IReadOnlyList<PullRequestInfo> AutoMergePrs { get; init; } = [];
    public IReadOnlyList<PullRequestInfo> MyPrs { get; init; } = [];
    public IReadOnlyList<PullRequestInfo> DraftPrs { get; init; } = [];
    public IReadOnlyList<PullRequestInfo> ReviewRequestedPrs { get; init; } = [];
    public IReadOnlyList<PullRequestInfo> TeamReviewRequestedPrs { get; init; } = [];
    public IReadOnlyList<PullRequestInfo> HotfixPrs { get; init; } = [];
    public IReadOnlyList<PullRequestInfo> DependabotPrs { get; init; } = [];
    public int FailedCICount => AutoMergePrs.Count(p => p.CIState == CIState.Failure);
    public int PendingCICount => AutoMergePrs.Count(p => p.CIState is CIState.Pending or CIState.Unknown);
    public int TotalCount => AutoMergePrs.Count + ReviewRequestedPrs.Count;
}

/// <summary>
/// Periodically polls GitHub and emits events when PR state changes.
/// </summary>
public sealed class PollingService : IDisposable
{
    private readonly GitHubService _github;
    private readonly AppSettings _settings;
    private readonly DiagnosticsLogger _logger;
    private System.Timers.Timer? _timer;
    private readonly SemaphoreSlim _pollLock = new(1, 1);

    internal Dictionary<string, PullRequestInfo> _previousAutoMerge = new();
    internal Dictionary<string, PullRequestInfo> _previousReviews = new();
    internal Dictionary<string, PullRequestInfo> _previousMyPrs = new();

    // Cache of last known definitive conflict state per PR+commit.
    // Key: "{prKey}:{headCommitSha}". Used to preserve conflict state when GitHub returns "UNKNOWN".
    private readonly Dictionary<string, bool> _conflictCache = new();

    /// <summary>Number of consecutive polls withheld because a section emptied out.</summary>
    internal int _withheldPollStreak;

    private static readonly TimeSpan MemoryTrimInterval = TimeSpan.FromMinutes(10);
    private DateTimeOffset _lastMemoryTrim = DateTimeOffset.UtcNow;

    /// <summary>Private bytes above which the trim ignores the window-visibility gate.</summary>
    private const long TrimRegardlessOfVisibilityBytes = 400L * 1024 * 1024;

    /// <summary>
    /// Optional gate for the periodic memory trim. The trim runs a blocking gen2 collection,
    /// so the host only allows it while the app is idle (window hidden).
    /// </summary>
    public Func<bool>? CanTrimMemory { get; set; }

    private readonly DateTimeOffset _startedAt = DateTimeOffset.UtcNow;

    public PollingService(GitHubService github, AppSettings settings, DiagnosticsLogger logger)
    {
        _github = github;
        _settings = settings;
        _logger = logger;
    }

    // ── Events ──────────────────────────────────────────────────────────

    /// <summary>Raised for every individual change detected.</summary>
    public event EventHandler<PrChangeEventArgs>? PrChanged;

    /// <summary>Raised after every completed poll with the full snapshot.</summary>
    public event EventHandler<PollSnapshot>? Polled;

    /// <summary>Raised when a poll cycle fails with an exception.</summary>
    public event Action<Exception>? PollFailed;

    /// <summary>Raised when an unseen @mention notification is detected in a PR. Args: (id, title, repo, prUrl).</summary>
    public event Action<string, string, string, string>? MentionDetected;

    /// <summary>The most recent snapshot (null before first poll).</summary>
    public PollSnapshot? LatestSnapshot { get; internal set; }

    // ── Lifecycle ───────────────────────────────────────────────────────

    public void Start()
    {
        // Fire immediately, then on interval
        _ = PollAsync();

        _timer = new System.Timers.Timer(_settings.PollingIntervalSeconds * 1000);
        _timer.Elapsed += (_, _) => _ = PollAsync();
        _timer.AutoReset = true;
        _timer.Start();
    }

    public void UpdateInterval(int seconds)
    {
        if (_timer is not null)
            _timer.Interval = seconds * 1000;
    }

    /// <summary>
    /// Trigger an immediate poll outside the regular interval.
    /// </summary>
    public Task RefreshAsync() => PollAsync();

    public void Dispose()
    {
        _timer?.Stop();
        _timer?.Dispose();
        _pollLock.Dispose();
    }

    // ── Core polling logic ──────────────────────────────────────────────

    private async Task PollAsync()
    {
        if (!await _pollLock.WaitAsync(0))
            return; // another poll is already in progress — skip
        try
        {
        try
        {
            // Fetch all my PRs in a single API call, then split by auto-merge flag
            var allMyPrs  = await _github.FetchAllMyPRsAsync(_settings.Organizations);

            bool showTeamSection = _settings.ShowTeamReviewSection;
            // Always classify team PRs (classifyTeams always true) so they can be
            // excluded from Awaiting My Review regardless of whether the section is shown.
            var reviewPrs   = await _github.FetchPRsAwaitingMyReviewAsync(_settings.Organizations, classifyTeams: true, currentUsername: _settings.GitHubUsername);
            var assignedPrs = await _github.FetchMyAssignedPRsAsync(_settings.Organizations);
            var hotfixPrs   = await _github.FetchHotfixPRsAsync(_settings.Organizations);

            var myPrKeys = allMyPrs.Select(p => p.Key).ToHashSet();
            var assignedPrKeys = assignedPrs.Select(p => p.Key).ToHashSet();

            // Only keep hotfix PRs that are owned by me or explicitly assigned to me.
            // This avoids showing release PRs where I was merely involved (e.g. reviewed/commented).
            hotfixPrs = FilterOwnedOrAssignedHotfixPrs(hotfixPrs, myPrKeys, assignedPrKeys);

            // Exclude hotfix PRs (release/* targets) from every other section to avoid duplication.
            // Hotfix PRs can be cherry-picked by a tool (so the "author" isn't really me) and still
            // show up as awaiting-my-review or assigned-to-me, which must not create a duplicate row.
            var hotfixKeys   = hotfixPrs.Select(p => p.Key).ToHashSet();
            var autoMergePrs = allMyPrs.Where(p => p.HasAutoMerge && !hotfixKeys.Contains(p.Key)).ToList();
            var myPrs        = allMyPrs.Where(p => !p.HasAutoMerge && !hotfixKeys.Contains(p.Key) && !p.IsDraft).ToList();
            var draftPrs     = allMyPrs.Where(p => !p.HasAutoMerge && !hotfixKeys.Contains(p.Key) && p.IsDraft).ToList();

            reviewPrs   = reviewPrs.Where(p => !hotfixKeys.Contains(p.Key)).ToList();
            assignedPrs = assignedPrs.Where(p => !hotfixKeys.Contains(p.Key)).ToList();

            // Split review PRs into direct-user requests and team-only requests
            var directReviewPrs = reviewPrs.Where(p => !p.IsTeamReviewRequested).ToList();
            var teamOnlyPrs     = reviewPrs.Where(p => p.IsTeamReviewRequested).ToList();

            // Split off dependabot PRs from direct review list
            var dependabotPrs = directReviewPrs
                .Where(p => p.Author.Equals("dependabot[bot]", StringComparison.OrdinalIgnoreCase)
                          || p.Author.Equals("dependabot", StringComparison.OrdinalIgnoreCase))
                .ToList();

            var dependabotKeys = dependabotPrs.Select(p => p.Key).ToHashSet();

            // Direct review list + assignee-only PRs (not authored by current user), excluding dependabot
            var combinedReviewPrs = directReviewPrs
                .Where(p => !dependabotKeys.Contains(p.Key))
                .Concat(assignedPrs.Where(p => !myPrKeys.Contains(p.Key) && !dependabotKeys.Contains(p.Key)))
                .DistinctBy(p => p.Key)
                .ToList();

            // Team section: only when enabled; otherwise drop team PRs entirely (not shown anywhere)
            // Also deduplicate: if the same PR is already in combinedReviewPrs (via direct review
            // request or assignee), don't show it again in team review requests.
            List<PullRequestInfo> teamReviewPrs;
            if (showTeamSection)
            {
                var combinedKeys = combinedReviewPrs.Select(p => p.Key).ToHashSet();
                teamReviewPrs = teamOnlyPrs.Where(p => !combinedKeys.Contains(p.Key)).ToList();
            }
            else
            {
                // Team PRs are hidden completely when the section is disabled
                teamReviewPrs = [];
            }

            // Resolve "UNKNOWN" mergeability using sticky cache
            autoMergePrs      = ResolveConflicts(autoMergePrs);
            myPrs             = ResolveConflicts(myPrs);
            draftPrs          = ResolveConflicts(draftPrs);
            combinedReviewPrs = ResolveConflicts(combinedReviewPrs);
            teamReviewPrs     = ResolveConflicts(teamReviewPrs);
            hotfixPrs         = ResolveConflicts(hotfixPrs);
            dependabotPrs     = ResolveConflicts(dependabotPrs);

            // Derive stacked-PR relations across every section, then group each section by stack
            ApplyStackRelations([
                .. autoMergePrs, .. myPrs, .. draftPrs, .. combinedReviewPrs,
                .. teamReviewPrs, .. hotfixPrs, .. dependabotPrs,
            ]);

            PruneConflictCache([
                .. autoMergePrs, .. myPrs, .. draftPrs, .. combinedReviewPrs,
                .. teamReviewPrs, .. hotfixPrs, .. dependabotPrs,
            ]);

            if (_settings.ShowStackRelations)
            {
                autoMergePrs      = OrderByStack(autoMergePrs);
                myPrs             = OrderByStack(myPrs);
                draftPrs          = OrderByStack(draftPrs);
                combinedReviewPrs = OrderByStack(combinedReviewPrs);
                teamReviewPrs     = OrderByStack(teamReviewPrs);
                hotfixPrs         = OrderByStack(hotfixPrs);
                dependabotPrs     = OrderByStack(dependabotPrs);
            }

            var allOpenPrKeys = allMyPrs.Select(p => p.Key).ToHashSet();

            var snapshot = new PollSnapshot
            {
                AutoMergePrs           = autoMergePrs,
                MyPrs                  = myPrs,
                DraftPrs               = draftPrs,
                ReviewRequestedPrs     = combinedReviewPrs,
                TeamReviewRequestedPrs = teamReviewPrs,
                HotfixPrs              = hotfixPrs,
                DependabotPrs          = dependabotPrs,
            };

            // A section that empties out completely from one poll to the next is far more often a
            // transient GitHub search glitch (exit 0, valid JSON, zero hits) than every PR in it
            // disappearing at once. Require a second confirming poll before publishing, so the UI
            // doesn't flip and notifications/statistics don't fire "merged" followed by "new".
            if (ShouldWithholdSnapshot(snapshot, out var emptied))
            {
                _logger.Warn($"PollingService: section(s) {emptied} emptied out in one poll — withholding until the next poll confirms it.");
                return;
            }

            DetectAutoMergeChanges(autoMergePrs, allOpenPrKeys);
            DetectReviewChanges(combinedReviewPrs);
            DetectMyPrsChanges(myPrs.Concat(draftPrs).ToList());

            LatestSnapshot = snapshot;
            Polled?.Invoke(this, snapshot);

            if (_settings.NotifyMentioned)
            {
                var monitoredOrgs = _settings.Organizations
                    .Select(o => o.Trim().ToLowerInvariant())
                    .Where(o => o.Length > 0)
                    .ToHashSet();
                var mentions = await _github.FetchMentionNotificationsAsync();
                foreach (var (id, title, repo, updatedAt, prUrl) in mentions)
                {
                    if (updatedAt < _startedAt) continue;
                    if (monitoredOrgs.Count > 0 && !monitoredOrgs.Contains(repo.Split('/')[0].ToLowerInvariant())) continue;
                    MentionDetected?.Invoke(id, title, repo, prUrl);
                    await _github.MarkNotificationReadAsync(id);
                }
            }

        }
        catch (Exception ex)
        {
            _logger.Error("PollingService poll failed.", ex);
            PollFailed?.Invoke(ex);
            // Swallow – we'll try again next interval.
        }
        }
        finally
        {
            MemoryDiagnostics.Log(_logger, "poll-end");
            MaybeTrimMemory();
            _pollLock.Release();
        }
    }

    /// <summary>
    /// Names of the sections that went from at least one PR to zero between two snapshots.
    /// </summary>
    internal static List<string> EmptiedSections(PollSnapshot previous, PollSnapshot current)
    {
        var emptied = new List<string>();
        Check("Auto-merge", previous.AutoMergePrs, current.AutoMergePrs);
        Check("My PRs", previous.MyPrs, current.MyPrs);
        Check("Drafts", previous.DraftPrs, current.DraftPrs);
        Check("Awaiting my review", previous.ReviewRequestedPrs, current.ReviewRequestedPrs);
        Check("Team review", previous.TeamReviewRequestedPrs, current.TeamReviewRequestedPrs);
        Check("Hotfixes", previous.HotfixPrs, current.HotfixPrs);
        Check("Dependabot", previous.DependabotPrs, current.DependabotPrs);
        return emptied;

        void Check(string name, IReadOnlyList<PullRequestInfo> before, IReadOnlyList<PullRequestInfo> after)
        {
            if (before.Count > 0 && after.Count == 0) emptied.Add(name);
        }
    }

    /// <summary>
    /// True when the poll result should be discarded because a section emptied out and no
    /// second poll has confirmed it yet.
    /// </summary>
    internal bool ShouldWithholdSnapshot(PollSnapshot snapshot, out string emptiedSections)
    {
        emptiedSections = "";
        if (LatestSnapshot is not { } previous)
        {
            _withheldPollStreak = 0;
            return false;
        }

        var emptied = EmptiedSections(previous, snapshot);
        if (emptied.Count == 0)
        {
            _withheldPollStreak = 0;
            return false;
        }

        emptiedSections = string.Join(", ", emptied);
        _withheldPollStreak++;
        return _withheldPollStreak < 2;
    }

    internal static List<PullRequestInfo> FilterOwnedOrAssignedHotfixPrs(
        IEnumerable<PullRequestInfo> hotfixPrs,
        IReadOnlySet<string> myPrKeys,
        IReadOnlySet<string> assignedPrKeys)
    {
        return hotfixPrs
            .Where(p => myPrKeys.Contains(p.Key) || assignedPrKeys.Contains(p.Key))
            .ToList();
    }

    /// <summary>
    /// Derives stacked-PR relations (gh-stack style) from the base/head branch names of all
    /// currently known open PRs: a PR is stacked on another when its base branch equals that
    /// PR's head branch in the same repository. Purely local — costs no extra API calls.
    /// Mutates the stack fields on every supplied instance (the same PR can appear in several lists).
    /// </summary>
    internal static void ApplyStackRelations(IReadOnlyList<PullRequestInfo> allPrs)
    {
        foreach (var pr in allPrs)
        {
            pr.StackParentKey = null;
            pr.StackParentNumber = 0;
            pr.StackParentUrl = "";
            pr.StackParentAuthor = "";
            pr.StackRootKey = pr.Key;
            pr.StackDepth = 0;
            pr.StackSize = 1;
        }

        var unique = allPrs
            .GroupBy(p => p.Key, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        var byHead = new Dictionary<string, PullRequestInfo>(StringComparer.OrdinalIgnoreCase);
        foreach (var pr in unique.Values)
        {
            if (string.IsNullOrWhiteSpace(pr.HeadRefName)) continue;
            byHead.TryAdd(BranchKey(pr.Repository, pr.HeadRefName), pr);
        }

        var parents = new Dictionary<string, PullRequestInfo>(StringComparer.OrdinalIgnoreCase);
        foreach (var pr in unique.Values)
        {
            if (string.IsNullOrWhiteSpace(pr.BaseRefName)) continue;
            if (byHead.TryGetValue(BranchKey(pr.Repository, pr.BaseRefName), out var parent)
                && !parent.Key.Equals(pr.Key, StringComparison.OrdinalIgnoreCase))
            {
                parents[pr.Key] = parent;
            }
        }

        var depths = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var roots = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pr in unique.Values)
        {
            var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var current = pr;
            int depth = 0;
            while (visited.Add(current.Key) && parents.TryGetValue(current.Key, out var parent))
            {
                current = parent;
                depth++;
            }
            depths[pr.Key] = depth;
            roots[pr.Key] = current.Key;
        }

        var sizes = roots.Values
            .GroupBy(r => r, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);

        foreach (var pr in allPrs)
        {
            if (!roots.TryGetValue(pr.Key, out var root)) continue;
            pr.StackRootKey = root;
            pr.StackDepth = depths[pr.Key];
            pr.StackSize = sizes.GetValueOrDefault(root, 1);
            if (parents.TryGetValue(pr.Key, out var parent))
            {
                pr.StackParentKey = parent.Key;
                pr.StackParentNumber = parent.Number;
                pr.StackParentUrl = parent.Url;
                pr.StackParentAuthor = parent.Author;
            }
        }
    }

    private static string BranchKey(string repository, string branch) => $"{repository}\u0000{branch}";

    /// <summary>
    /// Reorders a section so PRs belonging to the same stack appear consecutively,
    /// bottom PR first. The relative order of unrelated PRs (and of stacks as a whole)
    /// is preserved based on first appearance.
    /// </summary>
    internal static List<PullRequestInfo> OrderByStack(IReadOnlyList<PullRequestInfo> prs)
    {
        var groups = prs
            .GroupBy(p => p.StackRootKey ?? p.Key, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                g => g.Key,
                g => g.OrderBy(p => p.StackDepth).ThenBy(p => p.Number).ToList(),
                StringComparer.OrdinalIgnoreCase);

        var result = new List<PullRequestInfo>(prs.Count);
        var emitted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pr in prs)
        {
            var root = pr.StackRootKey ?? pr.Key;
            if (!emitted.Add(root)) continue;
            result.AddRange(groups[root]);
        }
        return result;
    }

    /// <summary>
    /// For any PR where GitHub returned "UNKNOWN" for mergeability, substitutes the last known
    /// cached conflict state (keyed by PR key + head commit SHA). Definitive states update the cache.
    /// </summary>
    private List<PullRequestInfo> ResolveConflicts(IEnumerable<PullRequestInfo> prs)
    {
        var result = new List<PullRequestInfo>();
        foreach (var pr in prs)
        {
            var cacheKey = $"{pr.Key}:{pr.HeadCommitSha}";
            if (!pr.IsMergeabilityUnknown)
            {
                // Definitive state — update cache and keep as-is
                _conflictCache[cacheKey] = pr.HasConflicts;
            }
            else if (_conflictCache.TryGetValue(cacheKey, out var cached))
            {
                // UNKNOWN but we have a cached value — apply it
                pr.HasConflicts = cached;
            }
            // else: UNKNOWN with no cache entry — keep HasConflicts = false (default)
            result.Add(pr);
        }
        return result;
    }

    /// <summary>
    /// Drops cache entries for PR/commit combinations that no longer appear in any section,
    /// so the cache cannot grow without bound over long-running sessions.
    /// </summary>
    private void PruneConflictCache(IEnumerable<PullRequestInfo> livePrs)
    {
        var live = livePrs.Select(p => $"{p.Key}:{p.HeadCommitSha}").ToHashSet(StringComparer.Ordinal);
        foreach (var staleKey in _conflictCache.Keys.Where(k => !live.Contains(k)).ToList())
            _conflictCache.Remove(staleKey);
    }

    private void MaybeTrimMemory()
    {
        if (DateTimeOffset.UtcNow - _lastMemoryTrim < MemoryTrimInterval) return;

        // The visibility gate avoids stuttering a window the user is looking at, but a process this
        // large needs reclaiming more than it needs a smooth frame.
        if (CanTrimMemory is { } gate && !gate() && !IsUnderMemoryPressure()) return;

        _lastMemoryTrim = DateTimeOffset.UtcNow;
        MemoryDiagnostics.TrimMemory(_logger, "poll");
    }

    private static bool IsUnderMemoryPressure()
    {
        try
        {
            using var process = System.Diagnostics.Process.GetCurrentProcess();
            return process.PrivateMemorySize64 > TrimRegardlessOfVisibilityBytes;
        }
        catch
        {
            return false;
        }
    }

    internal void DetectAutoMergeChanges(List<PullRequestInfo> current, HashSet<string>? allOpenPrKeys = null)
    {
        var currentDict = current.ToDictionary(p => p.Key);

        // New or changed PRs
        foreach (var pr in current)
        {
            if (_previousAutoMerge.TryGetValue(pr.Key, out var prev))
            {
                if (prev.CIState != pr.CIState)
                {
                    RaiseChange(pr, PrChangeKind.CIStatusChanged, prev.CIState);
                }
            }
            else
            {
                RaiseChange(pr, PrChangeKind.NewAutoMergePr);
            }
        }

        // Removed PRs: only notify when the PR is truly gone (merged/closed).
        // If the PR is still open (present in allOpenPrKeys) it just had auto-merge
        // disabled and will appear in My PRs — no notification needed.
        foreach (var key in _previousAutoMerge.Keys)
        {
            if (!currentDict.ContainsKey(key) && allOpenPrKeys?.Contains(key) != true)
            {
                RaiseChange(_previousAutoMerge[key], PrChangeKind.RemovedAutoMergePr);
            }
        }

        _previousAutoMerge = currentDict;
    }

    internal void DetectReviewChanges(List<PullRequestInfo> current)
    {
        var currentDict = current.ToDictionary(p => p.Key);

        foreach (var pr in current)
        {
            if (!_previousReviews.ContainsKey(pr.Key))
            {
                RaiseChange(pr, PrChangeKind.NewReviewRequested);
            }
        }

        foreach (var key in _previousReviews.Keys)
        {
            if (!currentDict.ContainsKey(key))
            {
                RaiseChange(_previousReviews[key], PrChangeKind.ReviewRequestRemoved);
            }
        }

        _previousReviews = currentDict;
    }

    internal void DetectMyPrsChanges(List<PullRequestInfo> current)
    {
        var currentDict = current.ToDictionary(p => p.Key);

        foreach (var pr in current)
        {
            if (_previousMyPrs.TryGetValue(pr.Key, out var prev))
            {
                if (prev.CIState != pr.CIState)
                    RaiseChange(pr, PrChangeKind.CIStatusChanged, prev.CIState);
            }
        }

        _previousMyPrs = currentDict;
    }

    private void RaiseChange(PullRequestInfo pr, PrChangeKind kind, CIState previousCI = CIState.Unknown)
    {
        PrChanged?.Invoke(this, new PrChangeEventArgs
        {
            PullRequest = pr,
            Kind = kind,
            PreviousCIState = previousCI,
        });
    }
}
