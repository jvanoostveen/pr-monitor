using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Net.Sockets;
using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace PrMonitor.Services;

public sealed class UpdateService
{
    private const string LatestReleaseUrl = "https://api.github.com/repos/jvanoostveen/pr-monitor/releases/latest";
    private const string RawChangelogUrl = "https://raw.githubusercontent.com/jvanoostveen/pr-monitor/main/CHANGELOG.md";
    private const string RepoBaseUrl = "https://github.com/jvanoostveen/pr-monitor";
    private const string ReleaseDownloadBaseUrl = "https://github.com/jvanoostveen/pr-monitor/releases/download";
    public const string ChangelogFileUrl = RepoBaseUrl + "/blob/main/CHANGELOG.md";

    private static readonly HttpClient HttpClient = CreateHttpClient();
    private static readonly HttpClient DownloadHttpClient = CreateDownloadHttpClient();
    private readonly DiagnosticsLogger _logger;

    public UpdateService(DiagnosticsLogger logger)
    {
        _logger = logger;
    }

    public async Task<UpdateChangelogResult?> GetRelevantChangelogAsync(
        string currentVersion,
        string latestVersion,
        CancellationToken cancellationToken = default)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, RawChangelogUrl);
            request.Headers.Add("Accept", "text/plain");

            using var response = await HttpClient.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                _logger.Warn($"UpdateService changelog fetch returned {(int)response.StatusCode} ({response.ReasonPhrase}).");
                return null;
            }

            var markdown = await response.Content.ReadAsStringAsync(cancellationToken);
            var result = ExtractRelevantChangelog(markdown, currentVersion, latestVersion);
            if (result is not null)
                return result;

            _logger.Warn($"UpdateService could not extract changelog range. Current='{currentVersion}', Latest='{latestVersion}'.");
            return null;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Only a real cancellation propagates; an HttpClient timeout is also a
            // TaskCanceledException and falls through to the failure branch below.
            _logger.Warn("UpdateService changelog fetch canceled.");
            throw;
        }
        catch (Exception ex)
        {
            _logger.Warn($"UpdateService changelog fetch failed. {DiagnosticsLogger.SummarizeException(ex)}");
            return null;
        }
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
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Only a real cancellation propagates; an HttpClient timeout is also a
            // TaskCanceledException and falls through to the failure branch below.
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
        var (output, stderr, exitCode) = await GhCli.RunAsync(
            _logger, "UpdateService", cancellationToken, "api", "repos/jvanoostveen/pr-monitor/releases/latest");

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
                ErrorMessage: null,
                ZipSha256: FindAssetSha256(root, ZipAssetName(latestVersionText)));
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

    public static string GetCurrentAppVersionText() => GetCurrentAppVersion();

    /// <summary>
    /// True when <paramref name="toVersion"/> is strictly newer than <paramref name="fromVersion"/>.
    /// Used to tell an installed update apart from a first run, an unchanged version, or a
    /// downgrade (e.g. a manually restored older exe), none of which should surface a changelog.
    /// </summary>
    public static bool IsUpgrade(string? fromVersion, string? toVersion)
    {
        if (string.IsNullOrWhiteSpace(fromVersion) || string.IsNullOrWhiteSpace(toVersion))
            return false;

        return TryParseSemanticVersion(fromVersion, out var from)
            && TryParseSemanticVersion(toVersion, out var to)
            && to > from;
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

    public static UpdateChangelogResult? ExtractRelevantChangelog(
        string markdownText,
        string currentVersion,
        string latestVersion)
    {
        if (string.IsNullOrWhiteSpace(markdownText)
            || !TryParseSemanticVersion(latestVersion, out var latestParsed))
        {
            return null;
        }

        var hasCurrentVersion = TryParseSemanticVersion(currentVersion, out var currentParsed);
        var sections = ParseChangelogSections(markdownText);
        if (sections.Count == 0)
            return null;

        var relevantSections = sections
            .Where(section => section.Version <= latestParsed
                && (!hasCurrentVersion || section.Version > currentParsed))
            .ToList();

        if (relevantSections.Count == 0)
        {
            relevantSections = sections
                .Where(section => section.Version == latestParsed)
                .ToList();
        }

        if (relevantSections.Count == 0)
            return null;

        var normalizedCurrent = NormalizeVersionText(currentVersion);
        var normalizedLatest = NormalizeVersionText(latestVersion);
        var title = relevantSections.Count == 1
            ? $"Changelog for v{relevantSections[0].VersionText}"
            : hasCurrentVersion
                ? $"Changelog from v{normalizedCurrent} to v{normalizedLatest}"
                : $"Changelog through v{normalizedLatest}";

        var content = string.Join(
            Environment.NewLine + Environment.NewLine,
            relevantSections.Select(section => section.Markdown.Trim()));

        // relevantSections[0] is the newest section (CHANGELOG.md lists versions newest-first).
        var headingLine = relevantSections[0].Markdown
            .Split('\n')[0]
            .TrimStart('#', ' ');
        var url = $"{ChangelogFileUrl}#{BuildGitHubHeadingAnchor(headingLine)}";

        return new UpdateChangelogResult(title, content, url);
    }

    /// <summary>
    /// Reproduces GitHub's Markdown heading-to-anchor slug algorithm (lowercase, strip
    /// punctuation other than spaces/hyphens, then turn spaces into hyphens) so a changelog
    /// link can jump straight to the matching "## [x.y.z] - date" entry on GitHub.
    /// </summary>
    internal static string BuildGitHubHeadingAnchor(string headingText)
    {
        var sb = new StringBuilder();
        foreach (var ch in headingText.ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(ch) || ch == ' ' || ch == '-')
                sb.Append(ch);
        }

        return sb.ToString().Replace(' ', '-');
    }

    public static bool TryExtractRelevantChangelog(
        string markdownText,
        string currentVersion,
        string latestVersion,
        out UpdateChangelogResult? result)
    {
        result = ExtractRelevantChangelog(markdownText, currentVersion, latestVersion);
        return result is not null;
    }

    private static List<ParsedChangelogSection> ParseChangelogSections(string markdownText)
    {
        var sections = new List<ParsedChangelogSection>();
        string? currentVersionText = null;
        Version? currentVersion = null;
        var buffer = new StringBuilder();

        foreach (var rawLine in markdownText.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            var line = rawLine.TrimEnd();
            if (TryParseChangelogVersionHeading(line, out var parsedVersionText, out var parsedVersion))
            {
                AddParsedChangelogSection(sections, currentVersionText, currentVersion, buffer);
                currentVersionText = parsedVersionText;
                currentVersion = parsedVersion;
                buffer.Clear();
            }

            if (currentVersion is not null)
                buffer.AppendLine(line);
        }

        AddParsedChangelogSection(sections, currentVersionText, currentVersion, buffer);
        return sections;
    }

    private static bool TryParseChangelogVersionHeading(string line, out string versionText, out Version version)
    {
        versionText = string.Empty;
        version = new Version(0, 0, 0, 0);

        if (!line.StartsWith("## [", StringComparison.Ordinal))
            return false;

        var closingBracket = line.IndexOf(']');
        if (closingBracket < 0)
            return false;

        var candidate = line[4..closingBracket].Trim();
        if (!TryParseSemanticVersion(candidate, out version))
            return false;

        versionText = NormalizeVersionText(candidate);
        return true;
    }

    private static void AddParsedChangelogSection(
        ICollection<ParsedChangelogSection> sections,
        string? versionText,
        Version? version,
        StringBuilder buffer)
    {
        if (string.IsNullOrWhiteSpace(versionText) || version is null || buffer.Length == 0)
            return;

        sections.Add(new ParsedChangelogSection(versionText, version, buffer.ToString().Trim()));
    }

    internal static string ZipAssetName(string version) => $"PrMonitor-{version}-win-x64.zip";

    /// <summary>
    /// The hex SHA-256 GitHub publishes for the release asset named exactly
    /// <paramref name="assetName"/> (<c>assets[].digest</c>, <c>"sha256:&lt;hex&gt;"</c>), or null
    /// when the asset or its digest is missing.
    /// </summary>
    internal static string? FindAssetSha256(JsonElement release, string assetName)
    {
        if (!release.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array)
            return null;

        foreach (var asset in assets.EnumerateArray())
        {
            if (asset.TryGetProperty("name", out var name)
                && name.ValueKind == JsonValueKind.String
                && string.Equals(name.GetString(), assetName, StringComparison.Ordinal)
                && asset.TryGetProperty("digest", out var digest)
                && digest.ValueKind == JsonValueKind.String
                && digest.GetString() is { } value
                && value.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
            {
                var hex = value["sha256:".Length..];
                return hex.Length == 64 && hex.All(Uri.IsHexDigit) ? hex.ToLowerInvariant() : null;
            }
        }

        return null;
    }

    /// <summary>The update extracted by the last successful download: its path and SHA-256.</summary>
    private (string Path, string Sha256)? _preparedUpdate;

    /// <summary>
    /// Downloads the release zip for <paramref name="version"/> to a temp folder, checks it
    /// against <paramref name="expectedZipSha256"/> (the digest GitHub publishes for the asset),
    /// extracts PrMonitor.exe, and returns the path to the extracted exe.
    /// Reports download progress (0–100) via <paramref name="progress"/>.
    /// Throws <see cref="InvalidDataException"/> when no digest is known or it does not match.
    /// </summary>
    public async Task<string> DownloadUpdateAsync(string version, string? expectedZipSha256, IProgress<int> progress, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(expectedZipSha256))
            throw new InvalidDataException("The release does not publish a SHA-256 digest for its download, so it cannot be verified.");

        var zipFileName = ZipAssetName(version);
        var zipUrl = $"{ReleaseDownloadBaseUrl}/v{version}/{zipFileName}";
        var tempDir = Path.Combine(Path.GetTempPath(), "PrMonitor_update");
        Directory.CreateDirectory(tempDir);

        // Use a unique file name per attempt so a locked/leftover zip from a previous failed
        // attempt (e.g. still being scanned by antivirus, or from a crashed prior run) can never
        // collide with the file this attempt needs to create.
        var zipPath = Path.Combine(tempDir, $"{Guid.NewGuid():N}_{zipFileName}");
        var extractedExePath = Path.Combine(tempDir, "PrMonitor_new.exe");

        CleanupStaleUpdateZips(tempDir);

        _logger.Info($"UpdateService downloading {zipUrl} → {zipPath}");

        using var response = await DownloadHttpClient.GetAsync(zipUrl, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();

        var totalBytes = response.Content.Headers.ContentLength ?? -1L;
        await using var contentStream = await response.Content.ReadAsStreamAsync(ct);
        long bytesRead = 0;
        using var zipHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        await using (var fileStream = new FileStream(zipPath, FileMode.Create, FileAccess.Write, FileShare.None, bufferSize: 81920, useAsync: true))
        {
            var buffer = new byte[81920];
            int read;
            while ((read = await contentStream.ReadAsync(buffer.AsMemory(0, buffer.Length), ct)) > 0)
            {
                await fileStream.WriteAsync(buffer.AsMemory(0, read), ct);
                zipHash.AppendData(buffer, 0, read);
                bytesRead += read;
                if (totalBytes > 0)
                    progress.Report((int)(bytesRead * 100 / totalBytes));
            }
        }

        var actualZipSha256 = Convert.ToHexStringLower(zipHash.GetHashAndReset());
        if (!string.Equals(actualZipSha256, expectedZipSha256, StringComparison.OrdinalIgnoreCase))
        {
            try { File.Delete(zipPath); } catch { /* non-critical */ }
            _logger.Warn($"UpdateService download of {zipFileName} failed verification: expected SHA-256 {expectedZipSha256}, got {actualZipSha256}.");
            throw new InvalidDataException("The downloaded update does not match the checksum GitHub publishes for it.");
        }

        _logger.Info($"UpdateService download complete and verified ({bytesRead} bytes). Extracting…");

        // A freshly-downloaded file can briefly be locked by antivirus/SmartScreen for scanning
        // right after the write stream closes, and a leftover extracted exe from a previous
        // attempt may still be locked too. Retry the whole extraction with backoff instead of
        // failing outright.
        await RetryOnFileLockedAsync(() =>
        {
            if (File.Exists(extractedExePath))
                File.Delete(extractedExePath);

            using var archive = ZipFile.OpenRead(zipPath);
            var entry = archive.GetEntry("PrMonitor.exe")
                ?? throw new FileNotFoundException("PrMonitor.exe not found in release zip.", zipPath);
            entry.ExtractToFile(extractedExePath, overwrite: true);
        }, ct);

        try { File.Delete(zipPath); } catch { /* non-critical */ }

        RecordPreparedUpdate(extractedExePath);
        _logger.Info($"UpdateService extraction complete → {extractedExePath}");
        return extractedExePath;
    }

    /// <summary>
    /// Remembers the extracted exe and its hash, so <see cref="StartUpdateProcess"/> can refuse
    /// a file that changed or vanished while it waited (possibly days) for the restart click.
    /// </summary>
    internal void RecordPreparedUpdate(string exePath) => _preparedUpdate = (exePath, HashFile(exePath));

    private static string HashFile(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }

    /// <summary>The file's SHA-256, or null when it is missing, locked or unreadable.</summary>
    private static string? TryHashFile(string path)
    {
        try
        {
            return HashFile(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// Best-effort removal of zip files left behind by previous failed update attempts.
    /// Files still locked (e.g. by antivirus) are silently skipped.
    /// </summary>
    private void CleanupStaleUpdateZips(string tempDir)
    {
        try
        {
            foreach (var file in Directory.EnumerateFiles(tempDir, "*_PrMonitor-*-win-x64.zip"))
            {
                try { File.Delete(file); }
                catch (Exception ex) { _logger.Warn($"UpdateService could not remove stale update zip '{file}'. {DiagnosticsLogger.SummarizeException(ex)}"); }
            }
        }
        catch (Exception ex)
        {
            _logger.Warn($"UpdateService stale update zip cleanup failed. {DiagnosticsLogger.SummarizeException(ex)}");
        }
    }

    /// <summary>
    /// Retries <paramref name="action"/> when it fails with an <see cref="IOException"/> caused
    /// by another process holding a lock on the file (common with antivirus scanning), using
    /// short exponential backoff. Rethrows on the final attempt or for unrelated errors.
    /// </summary>
    private async Task RetryOnFileLockedAsync(Action action, CancellationToken ct, int maxAttempts = 5)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                action();
                return;
            }
            catch (IOException ex) when (attempt < maxAttempts && IsFileLockedException(ex))
            {
                _logger.Warn($"UpdateService file locked, retrying ({attempt}/{maxAttempts})… {ex.Message}");
                await Task.Delay(TimeSpan.FromMilliseconds(400 * attempt), ct);
            }
        }
    }

    private static bool IsFileLockedException(IOException ex) =>
        ex.Message.Contains("being used by another process", StringComparison.OrdinalIgnoreCase)
        || ex.Message.Contains("cannot access the file", StringComparison.OrdinalIgnoreCase);

    private const string NewExeVariable = "PRMONITOR_NEW_EXE";
    private const string CurrentExeVariable = "PRMONITOR_CURRENT_EXE";

    /// <summary>
    /// The update script. It contains no paths: they arrive as the environment variables
    /// <see cref="NewExeVariable"/> and <see cref="CurrentExeVariable"/>, which reach cmd.exe as
    /// UTF-16 and are expanded once. Embedding them would break on any character the batch
    /// file's code page cannot hold, and expand a <c>%</c> in a path.
    /// </summary>
    internal static string BuildUpdateBatScript(int pid)
    {
        const string newExe = "%" + NewExeVariable + "%";
        const string currentExe = "%" + CurrentExeVariable + "%";
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
        // Back up old exe then replace; if the copy fails, put the old exe back so the user is
        // never left without one.
        sb.AppendLine($"if exist \"{currentExe}.old\" del /f \"{currentExe}.old\" >nul 2>&1");
        sb.AppendLine($"move /y \"{currentExe}\" \"{currentExe}.old\" >nul");
        sb.AppendLine($"copy /y \"{newExe}\" \"{currentExe}\" >nul");
        sb.AppendLine($"if errorlevel 1 move /y \"{currentExe}.old\" \"{currentExe}\" >nul");
        sb.AppendLine($"start \"\" \"{currentExe}\"");
        sb.AppendLine("timeout /t 2 /nobreak >nul");
        sb.AppendLine($"del /f \"{newExe}\" >nul 2>&1");
        sb.AppendLine("(goto) 2>nul & del \"%~f0\"");
        return sb.ToString();
    }

    /// <summary>
    /// Writes a bat launcher script that waits for the current process to exit,
    /// replaces the exe, and restarts the app. Returns false, launching nothing, when
    /// <paramref name="newExePath"/> is not the update the last download prepared, or the file
    /// changed or vanished since. Returns without shutting down — the caller is responsible
    /// for calling Application.Current.Shutdown() after a true result.
    /// </summary>
    public bool StartUpdateProcess(string newExePath)
    {
        if (_preparedUpdate is not { } prepared
            || !string.Equals(prepared.Path, newExePath, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(TryHashFile(newExePath), prepared.Sha256, StringComparison.Ordinal))
        {
            _logger.Warn($"UpdateService refused to install '{newExePath}': it is missing or changed since it was downloaded and verified.");
            return false;
        }

        var currentExePath = Environment.ProcessPath
            ?? Process.GetCurrentProcess().MainModule?.FileName
            ?? throw new InvalidOperationException("Cannot determine current executable path.");

        var batPath = Path.Combine(Path.GetTempPath(), "PrMonitor_update.bat");
        File.WriteAllText(batPath, BuildUpdateBatScript(Environment.ProcessId), Encoding.ASCII);
        _logger.Info($"UpdateService launching update script: {batPath}");

        Process.Start(new ProcessStartInfo("cmd.exe")
        {
            ArgumentList = { "/c", batPath },
            CreateNoWindow = true,
            UseShellExecute = false,
            Environment =
            {
                [NewExeVariable] = newExePath,
                [CurrentExeVariable] = currentExePath,
            },
        });
        return true;
    }
}

public sealed record UpdateChangelogResult(string Title, string Markdown, string? Url = null);

internal sealed record ParsedChangelogSection(string VersionText, Version Version, string Markdown);

public sealed record UpdateCheckResult(
    bool IsUpdateAvailable,
    string CurrentVersion,
    string? LatestVersionText,
    string? ReleaseUrl,
    string? ReleaseNotesUrl,
    string? ReleaseNotes,
    string? ErrorMessage,
    string? ZipSha256 = null);