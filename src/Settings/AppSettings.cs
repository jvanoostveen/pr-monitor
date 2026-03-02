using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PrBot.Settings;

public sealed class AppSettings
{
    private static readonly string SettingsDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "pr-bot");

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
