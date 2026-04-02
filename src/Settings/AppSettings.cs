using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using PrMonitor.Models;

namespace PrMonitor.Settings;

public sealed class AppSettings
{
    private static readonly string SettingsDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "pr-monitor");

    private static readonly string SettingsPath =
        Path.Combine(SettingsDir, "settings.json");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    /// <summary>
    /// GitHub organizations to monitor (e.g. ["my-org"]).
    /// </summary>
    public List<string> Organizations { get; set; } = [];

    /// <summary>
    /// How often to poll GitHub for updates, in seconds.
    /// </summary>
    public int PollingIntervalSeconds { get; set; } = 120;

    /// <summary>
    /// Whether the app should start automatically with Windows.
    /// </summary>
    public bool AutoStartWithWindows { get; set; } = true;

    /// <summary>
    /// Whether to use compact row layout (smaller padding and font).
    /// </summary>
    public bool CompactMode { get; set; } = false;

    /// <summary>
    /// Cached GitHub username (auto-detected via `gh api user`).
    /// </summary>
    public string? GitHubUsername { get; set; }

    /// <summary>Whether the "My Auto-Merge PRs" section is expanded in the window.</summary>
    public bool AutoMergeExpanded { get; set; } = true;

    /// <summary>Whether the "Awaiting My Review" section is expanded in the window.</summary>
    public bool ReviewExpanded { get; set; } = true;

    /// <summary>Whether the "Hotfixes" section is expanded in the window.</summary>
    public bool HotfixExpanded { get; set; } = true;

    /// <summary>Whether the "My PRs" (non-auto-merge) section is expanded in the window.</summary>
    public bool MyPrsExpanded { get; set; } = false;

    /// <summary>Whether the "Later" section is expanded in the window.</summary>
    public bool LaterExpanded { get; set; } = false;

    /// <summary>Whether the "Team Review Requests" section is expanded in the window.</summary>
    public bool TeamReviewExpanded { get; set; } = false;

    /// <summary>Whether the "Dependabot" section is expanded in the window.</summary>
    public bool DependabotExpanded { get; set; } = true;

    /// <summary>Whether the "My Draft PRs" section is expanded in the window.</summary>
    public bool DraftExpanded { get; set; } = false;

    /// <summary>Whether to show a separate "Team Review Requests" section (versus folding into Awaiting My Review).</summary>
    public bool ShowTeamReviewSection { get; set; } = true;

    /// <summary>Whether team review requests count towards the tray icon status (amber) when the section is enabled.</summary>
    public bool TeamReviewCountsForTrayIcon { get; set; } = false;

    /// <summary>Whether the PR window was visible when last toggled/exited.</summary>
    public bool MainWindowVisible { get; set; } = false;

    /// <summary>Last known window left position in WPF units.</summary>
    public double? MainWindowLeft { get; set; }

    /// <summary>Last known window top position in WPF units.</summary>
    public double? MainWindowTop { get; set; }

    /// <summary>Last known snapped corner (None/TopLeft/TopRight/BottomLeft/BottomRight).</summary>
    public string? MainWindowSnappedCorner { get; set; }

    /// <summary>Keys of PRs the user has hidden to the "Later" section.</summary>
    public HashSet<string> HiddenPrKeys { get; set; } = [];

    /// <summary>
    /// Tracks the last time each hidden PR key was seen in a successful poll.
    /// Keys unseen for longer than the cooldown period are eligible for removal.
    /// </summary>
    public Dictionary<string, DateTimeOffset> HiddenPrLastSeen { get; set; } = [];

    /// <summary>
    /// Snooze expiry timestamps for hidden PRs. DateTimeOffset.MaxValue means indefinite.
    /// </summary>
    public Dictionary<string, DateTimeOffset> SnoozedPrs { get; set; } = new();

    // ── Notification toggles ─────────────────────────────────────────────

    /// <summary>Whether to show a toast when a CI build fails.</summary>
    public bool NotifyCiFailed { get; set; } = true;

    /// <summary>Whether to show a toast when CI recovers from failure to success.</summary>
    public bool NotifyCiPassed { get; set; } = true;

    /// <summary>Whether to show a toast when a CI build encounters an error.</summary>
    public bool NotifyCiError { get; set; } = true;

    /// <summary>Whether to show a toast when a new PR review is requested.</summary>
    public bool NotifyReviewRequested { get; set; } = true;

    /// <summary>Whether to show a toast when an auto-merge PR is merged or closed.</summary>
    public bool NotifyPrMergedOrClosed { get; set; } = true;

    /// <summary>Whether to show a toast when flakiness analysis triggers an automatic rerun.</summary>
    public bool NotifyFlakinessRerun { get; set; } = true;

    /// <summary>Whether to show a toast when Copilot determines a CI failure is a real (non-flaky) failure.</summary>
    public bool NotifyFlakinessRealFailure { get; set; } = true;

    /// <summary>Whether to show a startup summary toast after the first successful poll.</summary>
    public bool NotifyStartupSummary { get; set; } = true;

    /// <summary>Whether to show a toast when @mentioned in a PR comment or description.</summary>
    public bool NotifyMentioned { get; set; } = true;

    /// <summary>Controls when toast notifications are shown: always, only when the window is closed, or never.</summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public NotificationMode NotificationMode { get; set; } = NotificationMode.Always;

    // ── Flakiness analysis ───────────────────────────────────────────────

    /// <summary>Whether to automatically analyze CI failures for flakiness and retry.</summary>
    public bool FlakinessAnalysisEnabled { get; set; } = true;

    /// <summary>Whether flakiness analysis should only run for auto-merge PRs.</summary>
    public bool FlakinessAutoMergeOnly { get; set; } = false;

    /// <summary>Maximum automatic reruns for flaky failures per PR.</summary>
    public int FlakinessMaxReruns { get; set; } = 3;

    /// <summary>Free-text project context injected into the AI system prompt to guide flakiness classification.</summary>
    public string FlakinessCustomHints { get; set; } = "";

    /// <summary>Learned and user-managed flakiness patterns.</summary>
    public List<FlakinessRule> FlakinessRules { get; set; } = [];

    /// <summary>Per-PR rerun counts. Key = "owner/repo#number".</summary>
    public Dictionary<string, RerunRecord> FlakinessRerunCounts { get; set; } = [];

    // ── Reviewer assignment ──────────────────────────────────────────────

    /// <summary>Recently picked reviewers, most-recent first. Limited to 10 entries.</summary>
    public List<string> RecentReviewers { get; set; } = [];

    /// <summary>Cached org-member list for the reviewer search dialog.</summary>
    public List<OrgMemberEntry> OrgMembersCache { get; set; } = [];

    /// <summary>When the org-member cache was last fetched. Null means never fetched.</summary>
    public DateTimeOffset? OrgMembersCachedAt { get; set; }

    // ── Diagnostics ──────────────────────────────────────────────────────

    /// <summary>When true, verbose window-placement traces are written to the log.</summary>
    public bool VerboseLogging { get; set; } = false;

    /// <summary>
    /// Load settings from disk, or return defaults if no file exists.
    /// </summary>
    public static AppSettings Load() => LoadFrom(SettingsPath);

    internal static AppSettings LoadFrom(string path)
    {
        if (!File.Exists(path))
            return new AppSettings();

        AppSettings? settings = null;
        try
        {
            var json = File.ReadAllText(path);
            settings = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions) ?? new AppSettings();
        }
        catch
        {
            return new AppSettings();
        }

        // Clean up rerun records older than 30 days
        var cutoff = DateTimeOffset.UtcNow.AddDays(-30);
        foreach (var key in settings.FlakinessRerunCounts.Keys
            .Where(k => settings.FlakinessRerunCounts[k].LastAttempt < cutoff)
            .ToList())
        {
            settings.FlakinessRerunCounts.Remove(key);
        }

        // Prune expired snooze timers (already woken up)
        var expiredSnoozes = settings.SnoozedPrs
            .Where(kv => kv.Value != DateTimeOffset.MaxValue && kv.Value <= DateTimeOffset.UtcNow)
            .Select(kv => kv.Key)
            .ToList();
        foreach (var k in expiredSnoozes)
            settings.SnoozedPrs.Remove(k);

        return settings;
    }

    /// <summary>
    /// Persist current settings to disk.
    /// </summary>
    public void Save() => SaveTo(SettingsPath);

    internal void SaveTo(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var json = JsonSerializer.Serialize(this, JsonOptions);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, json);
        File.Move(tmp, path, overwrite: true);
    }
}
