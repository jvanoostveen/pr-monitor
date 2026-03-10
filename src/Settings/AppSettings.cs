using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

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

    /// <summary>Whether the PR window was visible when last toggled/exited.</summary>
    public bool MainWindowVisible { get; set; } = false;

    /// <summary>Last known window left position in WPF units.</summary>
    public double? MainWindowLeft { get; set; }

    /// <summary>Last known window top position in WPF units.</summary>
    public double? MainWindowTop { get; set; }

    /// <summary>Keys of PRs the user has hidden to the "Later" section.</summary>
    public HashSet<string> HiddenPrKeys { get; set; } = [];

    /// <summary>
    /// Tracks the last time each hidden PR key was seen in a successful poll.
    /// Keys unseen for longer than the cooldown period are eligible for removal.
    /// </summary>
    public Dictionary<string, DateTimeOffset> HiddenPrLastSeen { get; set; } = [];

    /// <summary>
    /// Load settings from disk, or return defaults if no file exists.
    /// </summary>
    public static AppSettings Load()
    {
        if (!File.Exists(SettingsPath))
            return new AppSettings();

        try
        {
            var json = File.ReadAllText(SettingsPath);
            return JsonSerializer.Deserialize<AppSettings>(json, JsonOptions) ?? new AppSettings();
        }
        catch
        {
            return new AppSettings();
        }
    }

    /// <summary>
    /// Persist current settings to disk.
    /// </summary>
    public void Save()
    {
        Directory.CreateDirectory(SettingsDir);
        var json = JsonSerializer.Serialize(this, JsonOptions);
        File.WriteAllText(SettingsPath, json);
    }
}
