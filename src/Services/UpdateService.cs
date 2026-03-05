using System.Net.Http;
using System.Diagnostics;
using System.Reflection;
using System.Text.Json;

namespace PrMonitor.Services;

public sealed class UpdateService
{
    private const string LatestReleaseUrl = "https://api.github.com/repos/jvanoostveen/pr-monitor/releases/latest";

    private static readonly HttpClient HttpClient = CreateHttpClient();
    private readonly DiagnosticsLogger _logger;

    public UpdateService(DiagnosticsLogger logger)
    {
        _logger = logger;
    }

    public async Task<UpdateCheckResult> CheckForUpdatesAsync(CancellationToken cancellationToken = default)
    {
        var currentVersion = GetCurrentAppVersion();
        _logger.Info($"UpdateService check started. CurrentVersion={currentVersion}");

        try
        {
            var ghJson = await TryGetLatestReleaseViaGhAsync(cancellationToken);
            if (!string.IsNullOrWhiteSpace(ghJson))
            {
                return ParseReleaseResult(ghJson, currentVersion, source: "gh");
            }

            using var request = new HttpRequestMessage(HttpMethod.Get, LatestReleaseUrl);
            request.Headers.Add("Accept", "application/vnd.github+json");

            using var response = await HttpClient.SendAsync(request, cancellationToken);
            _logger.Info($"UpdateService GitHub response status={(int)response.StatusCode}");
            if (!response.IsSuccessStatusCode)
            {
                var errorBody = await response.Content.ReadAsStringAsync(cancellationToken);
                _logger.Warn($"UpdateService non-success status {(int)response.StatusCode}. Body: {errorBody}");
                return new UpdateCheckResult(
                    IsUpdateAvailable: false,
                    CurrentVersion: currentVersion,
                    LatestVersionText: null,
                    ReleaseUrl: null,
                    ErrorMessage: $"GitHub API returned {(int)response.StatusCode} ({response.ReasonPhrase}).");
            }

            var responseJson = await response.Content.ReadAsStringAsync(cancellationToken);
            return ParseReleaseResult(responseJson, currentVersion, source: "http");
        }
        catch (OperationCanceledException)
        {
            _logger.Warn("UpdateService check canceled.");
            throw;
        }
        catch (Exception ex)
        {
            _logger.Error("UpdateService check failed.", ex);
            return new UpdateCheckResult(
                IsUpdateAvailable: false,
                CurrentVersion: currentVersion,
                LatestVersionText: null,
                ReleaseUrl: null,
                ErrorMessage: "Unable to reach GitHub releases right now. See diagnostics log for details.");
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

    private async Task<string?> TryGetLatestReleaseViaGhAsync(CancellationToken cancellationToken)
    {
        var (output, stderr, exitCode) = await RunGhAsync("api repos/jvanoostveen/pr-monitor/releases/latest", cancellationToken);

        if (exitCode != 0)
        {
            _logger.Warn($"UpdateService gh release lookup failed (exit={exitCode}). stderr: {stderr?.Trim()}");
            return null;
        }

        if (string.IsNullOrWhiteSpace(output))
        {
            _logger.Warn("UpdateService gh release lookup returned empty output.");
            return null;
        }

        _logger.Info("UpdateService release lookup succeeded via gh.");
        return output;
    }

    private UpdateCheckResult ParseReleaseResult(string jsonText, string currentVersion, string source)
    {
        try
        {
            using var json = JsonDocument.Parse(jsonText);

            var root = json.RootElement;
            var tag = root.TryGetProperty("tag_name", out var tagNode) ? tagNode.GetString() : null;
            var htmlUrl = root.TryGetProperty("html_url", out var htmlNode) ? htmlNode.GetString() : null;

            if (string.IsNullOrWhiteSpace(tag) || string.IsNullOrWhiteSpace(htmlUrl))
            {
                _logger.Warn($"UpdateService response missing tag_name or html_url (source={source}).");
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
                _logger.Warn($"UpdateService could not parse version info. Current='{currentVersion}', Latest='{latestVersionText}', Source={source}");
                return new UpdateCheckResult(
                    IsUpdateAvailable: false,
                    CurrentVersion: currentVersion,
                    LatestVersionText: latestVersionText,
                    ReleaseUrl: htmlUrl,
                    ErrorMessage: "Unable to parse version information.");
            }

            var isUpdateAvailable = latestParsed > currentParsed;
            _logger.Info($"UpdateService check finished via {source}. Latest={latestVersionText}, IsUpdateAvailable={isUpdateAvailable}");

            return new UpdateCheckResult(
                IsUpdateAvailable: isUpdateAvailable,
                CurrentVersion: currentVersion,
                LatestVersionText: latestVersionText,
                ReleaseUrl: htmlUrl,
                ErrorMessage: null);
        }
        catch (JsonException ex)
        {
            _logger.Error($"UpdateService JSON parse failed (source={source}).", ex);
            return new UpdateCheckResult(
                IsUpdateAvailable: false,
                CurrentVersion: currentVersion,
                LatestVersionText: null,
                ReleaseUrl: null,
                ErrorMessage: "Unable to parse release information.");
        }
    }

    private async Task<(string? Output, string? Stderr, int ExitCode)> RunGhAsync(string arguments, CancellationToken cancellationToken)
    {
        try
        {
            using var process = new Process();
            process.StartInfo = new ProcessStartInfo
            {
                FileName = "gh",
                Arguments = arguments,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            process.Start();

            var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);

            await process.WaitForExitAsync(cancellationToken);

            var output = await outputTask;
            var stderr = await stderrTask;

            return (output, stderr, process.ExitCode);
        }
        catch (Exception ex)
        {
            _logger.Warn($"UpdateService failed to execute gh command. {DiagnosticsLogger.SummarizeException(ex)}");
            return (null, null, -1);
        }
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