using System.Net.Http;
using System.Reflection;
using System.Text.Json;

namespace PrMonitor.Services;

public sealed class UpdateService
{
    private const string LatestReleaseUrl = "https://api.github.com/repos/jvanoostveen/pr-monitor/releases/latest";

    private static readonly HttpClient HttpClient = CreateHttpClient();

    public async Task<UpdateCheckResult> CheckForUpdatesAsync(CancellationToken cancellationToken = default)
    {
        var currentVersion = GetCurrentAppVersion();

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, LatestReleaseUrl);
            request.Headers.Add("Accept", "application/vnd.github+json");

            using var response = await HttpClient.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return new UpdateCheckResult(
                    IsUpdateAvailable: false,
                    CurrentVersion: currentVersion,
                    LatestVersionText: null,
                    ReleaseUrl: null,
                    ErrorMessage: $"GitHub API returned {(int)response.StatusCode}.");
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var json = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);

            var root = json.RootElement;
            var tag = root.TryGetProperty("tag_name", out var tagNode) ? tagNode.GetString() : null;
            var htmlUrl = root.TryGetProperty("html_url", out var htmlNode) ? htmlNode.GetString() : null;

            if (string.IsNullOrWhiteSpace(tag) || string.IsNullOrWhiteSpace(htmlUrl))
            {
                return new UpdateCheckResult(
                    IsUpdateAvailable: false,
                    CurrentVersion: currentVersion,
                    LatestVersionText: null,
                    ReleaseUrl: null,
                    ErrorMessage: "Latest release data is missing required fields.");
            }

            var latestVersionText = NormalizeVersionText(tag);
            if (!TryParseSemanticVersion(currentVersion, out var currentParsed)
                || !TryParseSemanticVersion(latestVersionText, out var latestParsed))
            {
                return new UpdateCheckResult(
                    IsUpdateAvailable: false,
                    CurrentVersion: currentVersion,
                    LatestVersionText: latestVersionText,
                    ReleaseUrl: htmlUrl,
                    ErrorMessage: "Unable to parse version information.");
            }

            return new UpdateCheckResult(
                IsUpdateAvailable: latestParsed > currentParsed,
                CurrentVersion: currentVersion,
                LatestVersionText: latestVersionText,
                ReleaseUrl: htmlUrl,
                ErrorMessage: null);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return new UpdateCheckResult(
                IsUpdateAvailable: false,
                CurrentVersion: currentVersion,
                LatestVersionText: null,
                ReleaseUrl: null,
                ErrorMessage: "Unable to reach GitHub releases right now.");
        }
    }

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(8),
        };
        client.DefaultRequestHeaders.Add("User-Agent", "pr-monitor");
        return client;
    }

    private static string GetCurrentAppVersion()
    {
        var assembly = Assembly.GetEntryAssembly() ?? Assembly.GetExecutingAssembly();
        var informationalVersion = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion;

        if (!string.IsNullOrWhiteSpace(informationalVersion))
            return NormalizeVersionText(informationalVersion);

        var fileVersion = assembly.GetCustomAttribute<AssemblyFileVersionAttribute>()?.Version;
        if (!string.IsNullOrWhiteSpace(fileVersion))
            return NormalizeVersionText(fileVersion);

        return NormalizeVersionText(assembly.GetName().Version?.ToString() ?? "0.0.0");
    }

    private static string NormalizeVersionText(string versionText)
    {
        var normalized = versionText.Trim();
        if (normalized.StartsWith("v", StringComparison.OrdinalIgnoreCase))
            normalized = normalized[1..];

        var plusIndex = normalized.IndexOf('+');
        if (plusIndex >= 0)
            normalized = normalized[..plusIndex];

        var dashIndex = normalized.IndexOf('-');
        if (dashIndex >= 0)
            normalized = normalized[..dashIndex];

        return normalized;
    }

    private static bool TryParseSemanticVersion(string versionText, out Version version)
    {
        version = new Version(0, 0, 0, 0);
        if (string.IsNullOrWhiteSpace(versionText))
            return false;

        var parts = NormalizeVersionText(versionText)
            .Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        if (parts.Length == 0 || parts.Length > 4)
            return false;

        var numbers = new[] { 0, 0, 0, 0 };
        for (var i = 0; i < parts.Length; i++)
        {
            if (!int.TryParse(parts[i], out numbers[i]) || numbers[i] < 0)
                return false;
        }

        version = new Version(numbers[0], numbers[1], numbers[2], numbers[3]);
        return true;
    }
}

public sealed record UpdateCheckResult(
    bool IsUpdateAvailable,
    string CurrentVersion,
    string? LatestVersionText,
    string? ReleaseUrl,
    string? ErrorMessage);