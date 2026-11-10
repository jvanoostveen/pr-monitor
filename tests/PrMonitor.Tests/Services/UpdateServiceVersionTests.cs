using System.IO;
using PrMonitor.Services;
using Xunit;

namespace PrMonitor.Tests.Services;

public class UpdateServiceVersionTests
{
    [Theory]
    [InlineData("1.2.3",              "1.2.3")]
    [InlineData("v1.2.3",             "1.2.3")]
    [InlineData("V1.2.3",             "1.2.3")]
    [InlineData("1.2.3+build.001",    "1.2.3")]
    [InlineData("1.2.3-beta.1",       "1.2.3")]
    [InlineData("v1.2.3-rc.1+build",  "1.2.3")]
    [InlineData("  1.2.3  ",          "1.2.3")]
    public void NormalizeVersionText_StripsAllAffixes(string input, string expected)
    {
        var result = UpdateService.NormalizeVersionText(input);
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("1.2.3",   true, 1, 2, 3, 0)]
    [InlineData("1.2.3.4", true, 1, 2, 3, 4)]
    [InlineData("1.2",     true, 1, 2, 0, 0)]
    [InlineData("1",       true, 1, 0, 0, 0)]
    public void TryParseSemanticVersion_ValidVersions_ParseCorrectly(
        string input, bool expectedSuccess, int major, int minor, int patch, int revision)
    {
        var success = UpdateService.TryParseSemanticVersion(input, out var version);
        Assert.Equal(expectedSuccess, success);
        Assert.Equal(new Version(major, minor, patch, revision), version);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-a-version")]
    [InlineData("1.2.3.4.5")]
    [InlineData("1.x.3")]
    public void TryParseSemanticVersion_InvalidVersions_ReturnFalse(string input)
    {
        var success = UpdateService.TryParseSemanticVersion(input, out _);
        Assert.False(success);
    }

    [Fact]
    public void TryParseSemanticVersion_VPrefixedVersion_ParsesAfterNormalization()
    {
        var success = UpdateService.TryParseSemanticVersion("v2.0.1", out var version);
        Assert.True(success);
        Assert.Equal(new Version(2, 0, 1, 0), version);
    }

    [Fact]
    public void ParseReleaseResult_NewerVersion_ReturnsUpdateAvailable()
    {
        var digest = new string('c', 64);
        var json = $$"""
            {"tag_name":"v2.0.0","html_url":"https://github.com/owner/repo/releases/tag/v2.0.0",
             "assets":[{"name":"PrMonitor-2.0.0-win-x64.zip","digest":"sha256:{{digest}}"}]}
            """;
        var svc = new UpdateService(DiagnosticsLogger.Null);

        var result = svc.ParseReleaseResult(json, "1.0.0", "test");

        Assert.True(result.IsUpdateAvailable);
        Assert.Equal("2.0.0", result.LatestVersionText);
        Assert.Equal(digest, result.ZipSha256);
        Assert.Equal("https://github.com/jvanoostveen/pr-monitor/compare/v1.0.0...v2.0.0", result.ReleaseUrl);
        Assert.Equal("https://github.com/owner/repo/releases/tag/v2.0.0", result.ReleaseNotesUrl);
        Assert.Null(result.ErrorMessage);
    }

    [Fact]
    public void ParseReleaseResult_WithBody_PopulatesReleaseNotes()
    {
        var json = """{"tag_name":"v2.0.0","html_url":"https://github.com/owner/repo/releases/tag/v2.0.0","body":"## What's new\n- Feature A"}""";
        var svc = new UpdateService(DiagnosticsLogger.Null);

        var result = svc.ParseReleaseResult(json, "1.0.0", "test");

        Assert.True(result.IsUpdateAvailable);
        Assert.Equal("## What's new\n- Feature A", result.ReleaseNotes);
        Assert.Equal("https://github.com/owner/repo/releases/tag/v2.0.0", result.ReleaseNotesUrl);
    }

    [Fact]
    public void TryExtractRelevantChangelog_ReturnsVersionsBetweenCurrentAndLatest()
    {
        var markdown = """
# Changelog

## [Unreleased]

## [1.8.3] - 2026-04-02

### Fixed

- Fixed latest update banner text.

## [1.8.2] - 2026-03-31

### Changed

- Reduced verbose logging.

## [1.8.1] - 2026-03-30

### Added

- Added in-place auto-update.
""";

        var result = UpdateService.ExtractRelevantChangelog(markdown, "1.8.1", "1.8.3");

        Assert.NotNull(result);
        Assert.Equal("Changelog from v1.8.1 to v1.8.3", result!.Title);
        Assert.Contains("## [1.8.3] - 2026-04-02", result.Markdown);
        Assert.Contains("## [1.8.2] - 2026-03-31", result.Markdown);
        Assert.DoesNotContain("## [1.8.1] - 2026-03-30", result.Markdown);
        Assert.DoesNotContain("## [Unreleased]", result.Markdown);
        Assert.Equal(
            "https://github.com/jvanoostveen/pr-monitor/blob/main/CHANGELOG.md#183---2026-04-02",
            result.Url);
    }

    [Theory]
    [InlineData("[1.13.0] - 2026-08-19", "1130---2026-08-19")]
    [InlineData("[1.1.3]", "113")]
    public void BuildGitHubHeadingAnchor_MatchesGitHubSlugAlgorithm(string heading, string expectedAnchor)
    {
        var anchor = UpdateService.BuildGitHubHeadingAnchor(heading);
        Assert.Equal(expectedAnchor, anchor);
    }

    [Fact]
    public void TryExtractRelevantChangelog_FallsBackToLatestSection()
    {
        var markdown = """
## [1.8.3] - 2026-04-02

### Fixed

- Fixed latest update banner text.

## [1.8.2] - 2026-03-31

### Changed

- Reduced verbose logging.
""";

        var result = UpdateService.ExtractRelevantChangelog(markdown, "not-a-version", "1.8.3");

        Assert.NotNull(result);
        Assert.Equal("Changelog through v1.8.3", result!.Title);
        Assert.Contains("## [1.8.3] - 2026-04-02", result.Markdown);
        Assert.Contains("## [1.8.2] - 2026-03-31", result.Markdown);
    }

    [Fact]
    public void ParseReleaseResult_WithoutBody_ReleaseNotesIsNull()
    {
        var json = """{"tag_name":"v2.0.0","html_url":"https://github.com/owner/repo/releases/tag/v2.0.0"}""";
        var svc = new UpdateService(DiagnosticsLogger.Null);

        var result = svc.ParseReleaseResult(json, "1.0.0", "test");

        Assert.Null(result.ReleaseNotes);
    }


    [Fact]
    public void ParseReleaseResult_SameVersion_NoUpdateAvailable()
    {
        var json = """{"tag_name":"v1.6.1","html_url":"https://example.com/release"}""";
        var svc = new UpdateService(DiagnosticsLogger.Null);

        var result = svc.ParseReleaseResult(json, "1.6.1", "test");

        Assert.False(result.IsUpdateAvailable);
        Assert.Null(result.ErrorMessage);
    }

    [Fact]
    public void ParseReleaseResult_OlderRemoteVersion_NoUpdateAvailable()
    {
        var json = """{"tag_name":"v1.0.0","html_url":"https://example.com/release"}""";
        var svc = new UpdateService(DiagnosticsLogger.Null);

        var result = svc.ParseReleaseResult(json, "1.6.1", "test");

        Assert.False(result.IsUpdateAvailable);
    }

    [Fact]
    public void ParseReleaseResult_InvalidJson_ReturnsError()
    {
        var svc = new UpdateService(DiagnosticsLogger.Null);

        var result = svc.ParseReleaseResult("not-json", "1.0.0", "test");

        Assert.False(result.IsUpdateAvailable);
        Assert.NotNull(result.ErrorMessage);
    }

    [Fact]
    public void ParseReleaseResult_MissingRequiredFields_ReturnsError()
    {
        var json = """{"some_field":"value"}""";
        var svc = new UpdateService(DiagnosticsLogger.Null);

        var result = svc.ParseReleaseResult(json, "1.0.0", "test");

        Assert.False(result.IsUpdateAvailable);
        Assert.NotNull(result.ErrorMessage);
    }

    [Theory]
    [InlineData(12345)]
    [InlineData(99)]
    public void BuildUpdateBatScript_WaitsForPidAndTakesPathsFromEnvironment(int pid)
    {
        var script = UpdateService.BuildUpdateBatScript(pid);

        Assert.Contains($"PID eq {pid}", script);
        Assert.Contains("copy /y \"%PRMONITOR_NEW_EXE%\" \"%PRMONITOR_CURRENT_EXE%\"", script);
        // Pure ASCII: paths never pass through the batch file's code page.
        Assert.True(script.All(char.IsAscii));
    }

    [Fact]
    public void BuildUpdateBatScript_RestoresOldExeWhenCopyFails()
    {
        var script = UpdateService.BuildUpdateBatScript(1);

        var copy = script.IndexOf("copy /y", StringComparison.Ordinal);
        var restore = script.IndexOf("if errorlevel 1 move /y \"%PRMONITOR_CURRENT_EXE%.old\" \"%PRMONITOR_CURRENT_EXE%\"", StringComparison.Ordinal);
        Assert.True(copy >= 0 && restore > copy);
    }

    [Fact]
    public void FindAssetSha256_ReturnsDigestOfTheExactlyNamedAsset()
    {
        var hex = new string('a', 64);
        var other = new string('b', 64);
        using var release = System.Text.Json.JsonDocument.Parse($$"""
            { "assets": [
                { "name": "PrMonitor-2.0.0-win-x64.zip.sig", "digest": "sha256:{{other}}" },
                { "name": "PrMonitor-2.0.0-win-x64.zip", "digest": "sha256:{{hex.ToUpperInvariant()}}" }
            ] }
            """);

        Assert.Equal(hex, UpdateService.FindAssetSha256(release.RootElement, "PrMonitor-2.0.0-win-x64.zip"));
    }

    [Theory]
    [InlineData("""{ "tag_name": "v2.0.0" }""")]
    [InlineData("""{ "assets": [ { "name": "PrMonitor-2.0.0-win-x64.zip" } ] }""")]
    [InlineData("""{ "assets": [ { "name": "PrMonitor-2.0.0-win-x64.zip", "digest": null } ] }""")]
    [InlineData("""{ "assets": [ { "name": "PrMonitor-2.0.0-win-x64.zip", "digest": "md5:0123456789abcdef0123456789abcdef" } ] }""")]
    [InlineData("""{ "assets": [ { "name": "PrMonitor-2.0.0-win-x64.zip", "digest": "sha256:not-hex" } ] }""")]
    [InlineData("""{ "assets": [ { "name": "PrMonitor-2.0.0-win-x64.zip.sig", "digest": "sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa" } ] }""")]
    public void FindAssetSha256_MissingOrUnusableDigest_ReturnsNull(string json)
    {
        using var release = System.Text.Json.JsonDocument.Parse(json);

        Assert.Null(UpdateService.FindAssetSha256(release.RootElement, "PrMonitor-2.0.0-win-x64.zip"));
    }

    [Fact]
    public void StartUpdateProcess_FileChangedSinceDownload_RefusesToInstall()
    {
        var exe = Path.Combine(Path.GetTempPath(), $"prupdate_{Guid.NewGuid():N}.exe");
        try
        {
            File.WriteAllText(exe, "verified");
            var svc = new UpdateService(DiagnosticsLogger.Null);
            svc.RecordPreparedUpdate(exe);
            File.WriteAllText(exe, "tampered");

            Assert.False(svc.StartUpdateProcess(exe));
        }
        finally { File.Delete(exe); }
    }

    [Fact]
    public void StartUpdateProcess_FileDeletedSinceDownload_RefusesToInstall()
    {
        var exe = Path.Combine(Path.GetTempPath(), $"prupdate_{Guid.NewGuid():N}.exe");
        File.WriteAllText(exe, "verified");
        var svc = new UpdateService(DiagnosticsLogger.Null);
        svc.RecordPreparedUpdate(exe);
        File.Delete(exe);

        Assert.False(svc.StartUpdateProcess(exe));
    }

    [Fact]
    public void StartUpdateProcess_FileLocked_RefusesToInstall()
    {
        var exe = Path.Combine(Path.GetTempPath(), $"prupdate_{Guid.NewGuid():N}.exe");
        try
        {
            File.WriteAllText(exe, "verified");
            var svc = new UpdateService(DiagnosticsLogger.Null);
            svc.RecordPreparedUpdate(exe);
            using var lockHandle = new FileStream(exe, FileMode.Open, FileAccess.Read, FileShare.None);

            Assert.False(svc.StartUpdateProcess(exe));
        }
        finally { File.Delete(exe); }
    }

    [Fact]
    public void StartUpdateProcess_NothingDownloaded_RefusesToInstall()
    {
        var svc = new UpdateService(DiagnosticsLogger.Null);

        Assert.False(svc.StartUpdateProcess(Path.Combine(Path.GetTempPath(), "PrMonitor_new.exe")));
    }

    [Theory]
    [InlineData("1.2.0", "1.3.0", true)]
    [InlineData("1.2.0", "1.2.1", true)]
    [InlineData("v1.2.0", "1.10.0", true)]
    [InlineData("1.2.0", "1.2.0", false)]
    [InlineData("1.3.0", "1.2.0", false)]
    [InlineData(null, "1.2.0", false)]
    [InlineData("", "1.2.0", false)]
    [InlineData("1.2.0", null, false)]
    [InlineData("not-a-version", "1.2.0", false)]
    public void IsUpgrade_OnlyTrueForAStrictlyNewerVersion(string? from, string? to, bool expected)
    {
        Assert.Equal(expected, UpdateService.IsUpgrade(from, to));
    }

    [Fact]
    public void BuildUpdateBatScript_BacksUpOldExeBeforeCopy()
    {
        var script = UpdateService.BuildUpdateBatScript(1);

        // Should move old exe to .old before copying
        Assert.Contains("move /y \"%PRMONITOR_CURRENT_EXE%\" \"%PRMONITOR_CURRENT_EXE%.old\"", script);
    }

    [Fact]
    public void BuildUpdateBatScript_StartsNewExeAfterCopy()
    {
        var script = UpdateService.BuildUpdateBatScript(1);

        // start "" launches the new exe
        Assert.Contains("start \"\" \"%PRMONITOR_CURRENT_EXE%\"", script);
    }

    [Fact]
    public void BuildUpdateBatScript_SelfDeletes()
    {
        var script = UpdateService.BuildUpdateBatScript(1);

        // The (goto) 2>nul & del "%~f0" idiom self-deletes the bat
        Assert.Contains("%~f0", script);
    }
}
