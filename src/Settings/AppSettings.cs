using System.IO;
using System.Threading;
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

    private static readonly AsyncLocal<string?> SettingsPathOverride = new();

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
    public bool MyPrsExpanded { get; set; } = true;

    /// <summary>Whether the "Later" section is expanded in the window.</summary>
    public bool LaterExpanded { get; set; } = false;

    /// <summary>Whether the "Team Review Requests" section is expanded in the window.</summary>
    public bool TeamReviewExpanded { get; set; } = false;

    /// <summary>Whether the "Dependabot" section is expanded in the window.</summary>
    public bool DependabotExpanded { get; set; } = false;

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
    /// Snooze expiry timestamps for hidden PRs. DateTimeOffset.MaxValue means indefinite.
    /// </summary>
    public Dictionary<string, DateTimeOffset> SnoozedPrs { get; set; } = new();

    /// <summary>
    /// Keys hidden via the explicit "Hide" action (not shown in the Later section).
    /// </summary>
    public HashSet<string> ManuallyHiddenPrKeys { get; set; } = [];

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
    [JsonConverter(typeof(NotificationModeTolerantConverter))]
    public NotificationMode NotificationMode { get; set; } = NotificationMode.Always;

    // ── Flakiness analysis ───────────────────────────────────────────────

    /// <summary>Whether to automatically analyze CI failures for flakiness and retry.</summary>
    public bool FlakinessAnalysisEnabled { get; set; } = true;

    /// <summary>Whether flakiness analysis should only run for auto-merge PRs.</summary>
    public bool FlakinessAutoMergeOnly { get; set; } = false;

    /// <summary>Maximum automatic reruns for flaky failures per PR.</summary>
    public int FlakinessMaxReruns { get; set; } = 3;

    // ── Auto-merge ───────────────────────────────────────────────────────

    /// <summary>Merge method to use when enabling auto-merge. One of "merge", "squash", "rebase".</summary>
    public string AutoMergeMergeMethod { get; set; } = "merge";

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
    public static AppSettings Load() => LoadFrom(GetSettingsPath());

    /// <summary>
    /// Test helper: temporarily override the default settings path for the current async flow.
    /// </summary>
    internal static IDisposable UseSettingsPathOverride(string path)
    {
        var previousPath = SettingsPathOverride.Value;
        SettingsPathOverride.Value = path;
        return new ActionOnDispose(() => SettingsPathOverride.Value = previousPath);
    }

    internal static AppSettings LoadFrom(string path)
    {
        if (!File.Exists(path))
            return new AppSettings();

        var settings = TryDeserialize(path);

        // Resilience: if the primary file is corrupted/partial, recover from last known good backup.
        if (settings is null)
            settings = TryDeserialize(path + ".bak");

        if (settings is null)
            return new AppSettings();

        // Clean up rerun records older than 30 days
        settings.Organizations ??= [];
        settings.HiddenPrKeys ??= [];
        settings.ManuallyHiddenPrKeys ??= [];
        settings.SnoozedPrs ??= new Dictionary<string, DateTimeOffset>();
        settings.FlakinessRules ??= [];
        settings.FlakinessRerunCounts ??= [];
        settings.RecentReviewers ??= [];
        settings.OrgMembersCache ??= [];
        settings.FlakinessCustomHints ??= "";

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

        // Legacy migration: in older builds all hidden keys represented Later items.
        // Convert hidden keys without explicit hide markers into indefinite snoozes.
        foreach (var key in settings.HiddenPrKeys
            .Where(k => !settings.SnoozedPrs.ContainsKey(k) && !settings.ManuallyHiddenPrKeys.Contains(k))
            .ToList())
        {
            settings.SnoozedPrs[key] = DateTimeOffset.MaxValue;
        }

        // Keep HiddenPrKeys as the authoritative union used by filtering.
        foreach (var key in settings.SnoozedPrs.Keys)
            settings.HiddenPrKeys.Add(key);
        foreach (var key in settings.ManuallyHiddenPrKeys)
            settings.HiddenPrKeys.Add(key);

        // Clean up stale markers that no longer exist in HiddenPrKeys.
        settings.ManuallyHiddenPrKeys = settings.ManuallyHiddenPrKeys
            .Where(settings.HiddenPrKeys.Contains)
            .ToHashSet(StringComparer.Ordinal);

        return settings;
    }

    /// <summary>
    /// Persist current settings to disk.
    /// </summary>
    public void Save() => SaveTo(GetSettingsPath());

    internal void SaveTo(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var json = JsonSerializer.Serialize(this, JsonOptions);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, json);

        // Keep a best-effort backup of the previous good settings file.
        if (File.Exists(path))
            File.Copy(path, path + ".bak", overwrite: true);

        File.Move(tmp, path, overwrite: true);
    }

    private static AppSettings? TryDeserialize(string path)
    {
        if (!File.Exists(path))
            return null;

        try
        {
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<AppSettings>(json, JsonOptions);
        }
        catch
        {
            return null;
        }
    }

    private static string GetSettingsPath() => SettingsPathOverride.Value ?? SettingsPath;

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

internal sealed class NotificationModeTolerantConverter : JsonConverter<NotificationMode>
{
    public override NotificationMode Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String)
        {
            var value = reader.GetString()?.Trim();
            if (string.IsNullOrWhiteSpace(value))
                return NotificationMode.Always;

            if (value.Equals("Always", StringComparison.OrdinalIgnoreCase))
                return NotificationMode.Always;
            if (value.Equals("WhenWindowClosed", StringComparison.OrdinalIgnoreCase)
                || value.Equals("OnlyWhenWindowClosed", StringComparison.OrdinalIgnoreCase))
                return NotificationMode.WhenWindowClosed;
            if (value.Equals("Never", StringComparison.OrdinalIgnoreCase))
                return NotificationMode.Never;

            return NotificationMode.Always;
        }

        if (reader.TokenType == JsonTokenType.Number && reader.TryGetInt32(out var numeric)
            && Enum.IsDefined(typeof(NotificationMode), numeric))
        {
            return (NotificationMode)numeric;
        }

        return NotificationMode.Always;
    }

    public override void Write(Utf8JsonWriter writer, NotificationMode value, JsonSerializerOptions options)
    {
        writer.WriteStringValue(value.ToString());
    }
}
