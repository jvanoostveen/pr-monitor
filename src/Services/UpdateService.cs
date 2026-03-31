using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Net.Sockets;
using System.Diagnostics;
using System.Reflection;
using System.Text;
using System.Text.Json;

namespace PrMonitor.Services;

public sealed class UpdateService
{
    private const string LatestReleaseUrl = "https://api.github.com/repos/jvanoostveen/pr-monitor/releases/latest";
    private const string RepoBaseUrl = "https://github.com/jvanoostveen/pr-monitor";
    private const string ReleaseDownloadBaseUrl = "https://github.com/jvanoostveen/pr-monitor/releases/download";

    private static readonly HttpClient HttpClient = CreateHttpClient();
    private static readonly HttpClient DownloadHttpClient = CreateDownloadHttpClient();
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
                    ReleaseNotesUrl: null,
                    ReleaseNotes: null,
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
                ReleaseNotesUrl: null,
                ReleaseNotes: null,
                ErrorMessage: "Unable to reach GitHub releases right now. See diagnostics log for details.");
        }
    }

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient(new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(15),
        })
        {
            Timeout = TimeSpan.FromSeconds(8),
        };
        client.DefaultRequestHeaders.Add("User-Agent", "pr-monitor");
        return client;
    }

    private static HttpClient CreateDownloadHttpClient()
    {
        var client = new HttpClient(new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(15),
            AllowAutoRedirect = true,
        })
        {
            Timeout = TimeSpan.FromMinutes(5),
        };
        client.DefaultRequestHeaders.Add("User-Agent", "pr-monitor");
        return client;
    }

    private async Task<string?> TryGetLatestReleaseViaGhAsync(CancellationToken cancellationToken)
    {
        var (output, stderr, exitCode) = await RunGhAsync(cancellationToken, "api", "repos/jvanoostveen/pr-monitor/releases/latest");

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

    internal UpdateCheckResult ParseReleaseResult(string jsonText, string currentVersion, string source)
    {
        try
        {
            using var json = JsonDocument.Parse(jsonText);

            var root = json.RootElement;
            var tag = root.TryGetProperty("tag_name", out var tagNode) ? tagNode.GetString() : null;
            var htmlUrl = root.TryGetProperty("html_url", out var htmlNode) ? htmlNode.GetString() : null;
            var releaseNotes = root.TryGetProperty("body", out var bodyNode) ? bodyNode.GetString() : null;

            if (string.IsNullOrWhiteSpace(tag) || string.IsNullOrWhiteSpace(htmlUrl))
            {
                _logger.Warn($"UpdateService response missing tag_name or html_url (source={source}).");
                return new UpdateCheckResult(
                    IsUpdateAvailable: false,
                    CurrentVersion: currentVersion,
                    LatestVersionText: null,
                    ReleaseUrl: null,
                    ReleaseNotesUrl: null,
                    ReleaseNotes: null,
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
                    ReleaseNotesUrl: htmlUrl,
                    ReleaseNotes: releaseNotes,
                    ErrorMessage: "Unable to parse version information.");
            }

            var isUpdateAvailable = latestParsed > currentParsed;
            _logger.Info($"UpdateService check finished via {source}. Latest={latestVersionText}, IsUpdateAvailable={isUpdateAvailable}");

            var releaseUrl = isUpdateAvailable
                ? $"{RepoBaseUrl}/compare/v{currentVersion}...v{latestVersionText}"
                : htmlUrl;

            return new UpdateCheckResult(
                IsUpdateAvailable: isUpdateAvailable,
                CurrentVersion: currentVersion,
                LatestVersionText: latestVersionText,
                ReleaseUrl: releaseUrl,
                ReleaseNotesUrl: htmlUrl,
                ReleaseNotes: releaseNotes,
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
                ReleaseNotesUrl: null,
                ReleaseNotes: null,
                ErrorMessage: "Unable to parse release information.");
        }
    }

    private async Task<(string? Output, string? Stderr, int ExitCode)> RunGhAsync(CancellationToken cancellationToken, params string[] arguments)
    {
        try
        {
            using var process = new Process();
            process.StartInfo = new ProcessStartInfo
            {
                FileName = "gh",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            foreach (var arg in arguments)
                process.StartInfo.ArgumentList.Add(arg);

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

    internal static string NormalizeVersionText(string versionText)
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

    internal static bool TryParseSemanticVersion(string versionText, out Version version)
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

    /// <summary>
    /// Downloads the release zip for <paramref name="version"/> to a temp folder,
    /// extracts PrMonitor.exe, and returns the path to the extracted exe.
    /// Reports download progress (0–100) via <paramref name="progress"/>.
    /// </summary>
    public async Task<string> DownloadUpdateAsync(string version, IProgress<int> progress, CancellationToken ct = default)
    {
        var zipFileName = $"PrMonitor-{version}-win-x64.zip";
        var zipUrl = $"{ReleaseDownloadBaseUrl}/v{version}/{zipFileName}";
        var tempDir = Path.Combine(Path.GetTempPath(), "PrMonitor_update");
        Directory.CreateDirectory(tempDir);
        var zipPath = Path.Combine(tempDir, zipFileName);
        var extractedExePath = Path.Combine(tempDir, "PrMonitor_new.exe");

        _logger.Info($"UpdateService downloading {zipUrl} → {zipPath}");

        using var response = await DownloadHttpClient.GetAsync(zipUrl, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();

        var totalBytes = response.Content.Headers.ContentLength ?? -1L;
        await using var contentStream = await response.Content.ReadAsStreamAsync(ct);
        await using var fileStream = new FileStream(zipPath, FileMode.Create, FileAccess.Write, FileShare.None, bufferSize: 81920, useAsync: true);

        var buffer = new byte[81920];
        long bytesRead = 0;
        int read;
        while ((read = await contentStream.ReadAsync(buffer.AsMemory(0, buffer.Length), ct)) > 0)
        {
            await fileStream.WriteAsync(buffer.AsMemory(0, read), ct);
            bytesRead += read;
            if (totalBytes > 0)
                progress.Report((int)(bytesRead * 100 / totalBytes));
        }

        _logger.Info($"UpdateService download complete ({bytesRead} bytes). Extracting…");

        if (File.Exists(extractedExePath))
            File.Delete(extractedExePath);

        using (var archive = ZipFile.OpenRead(zipPath))
        {
            var entry = archive.GetEntry("PrMonitor.exe")
                ?? throw new FileNotFoundException("PrMonitor.exe not found in release zip.", zipPath);
            entry.ExtractToFile(extractedExePath, overwrite: true);
        }

        try { File.Delete(zipPath); } catch { /* non-critical */ }

        _logger.Info($"UpdateService extraction complete → {extractedExePath}");
        return extractedExePath;
    }

    internal static string BuildUpdateBatScript(int pid, string newExePath, string currentExePath)
    {
        var sb = new StringBuilder();
        sb.AppendLine("@echo off");
        sb.AppendLine("setlocal");
        sb.AppendLine("set RETRIES=0");
        sb.AppendLine(":WAIT");
        sb.AppendLine("set /a RETRIES+=1");
        sb.AppendLine($"tasklist /fi \"PID eq {pid}\" 2>nul | find /i \"PrMonitor\" >nul 2>&1");
        sb.AppendLine("if not errorlevel 1 (");
        sb.AppendLine("    if %RETRIES% lss 30 (");
        sb.AppendLine("        timeout /t 1 /nobreak >nul");
        sb.AppendLine("        goto WAIT");
        sb.AppendLine("    )");
        sb.AppendLine(")");
        // Back up old exe then replace
        sb.AppendLine($"if exist \"{currentExePath}.old\" del /f \"{currentExePath}.old\" >nul 2>&1");
        sb.AppendLine($"move /y \"{currentExePath}\" \"{currentExePath}.old\" >nul");
        sb.AppendLine($"copy /y \"{newExePath}\" \"{currentExePath}\" >nul");
        sb.AppendLine($"start \"\" \"{currentExePath}\"");
        sb.AppendLine("timeout /t 2 /nobreak >nul");
        sb.AppendLine($"del /f \"{newExePath}\" >nul 2>&1");
        sb.AppendLine("(goto) 2>nul & del \"%~f0\"");
        return sb.ToString();
    }

    /// <summary>
    /// Writes a bat launcher script that waits for the current process to exit,
    /// replaces the exe, and restarts the app. Returns without shutting down —
    /// the caller is responsible for calling Application.Current.Shutdown().
    /// </summary>
    public void StartUpdateProcess(string newExePath)
    {
        var currentExePath = Environment.ProcessPath
            ?? Process.GetCurrentProcess().MainModule?.FileName
            ?? throw new InvalidOperationException("Cannot determine current executable path.");

        var pid = Environment.ProcessId;
        var batPath = Path.Combine(Path.GetTempPath(), "PrMonitor_update.bat");
        var script = BuildUpdateBatScript(pid, newExePath, currentExePath);

        File.WriteAllText(batPath, script, Encoding.ASCII);
        _logger.Info($"UpdateService launching update script: {batPath}");

        Process.Start(new ProcessStartInfo("cmd.exe")
        {
            ArgumentList = { "/c", batPath },
            CreateNoWindow = true,
            UseShellExecute = false,
        });
    }
}

public sealed record UpdateCheckResult(
    bool IsUpdateAvailable,
    string CurrentVersion,
    string? LatestVersionText,
    string? ReleaseUrl,
    string? ReleaseNotesUrl,
    string? ReleaseNotes,
    string? ErrorMessage);