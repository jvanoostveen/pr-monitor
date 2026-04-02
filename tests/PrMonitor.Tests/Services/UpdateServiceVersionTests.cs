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
        var json = """{"tag_name":"v2.0.0","html_url":"https://github.com/owner/repo/releases/tag/v2.0.0"}""";
        var svc = new UpdateService(DiagnosticsLogger.Null);

        var result = svc.ParseReleaseResult(json, "1.0.0", "test");

        Assert.True(result.IsUpdateAvailable);
        Assert.Equal("2.0.0", result.LatestVersionText);
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
    [InlineData(12345, "C:\\temp\\PrMonitor_new.exe", "C:\\Program Files\\PrMonitor.exe")]
    [InlineData(99, "D:\\update\\new.exe", "C:\\tools\\PrMonitor.exe")]
    public void BuildUpdateBatScript_ContainsExpectedPathsAndPid(int pid, string newExe, string currentExe)
    {
        var script = UpdateService.BuildUpdateBatScript(pid, newExe, currentExe);

        Assert.Contains($"PID eq {pid}", script);
        Assert.Contains(newExe, script);
        Assert.Contains(currentExe, script);
        Assert.Contains("copy /y", script);
        Assert.Contains("del", script);
    }

    [Fact]
    public void BuildUpdateBatScript_BacksUpOldExeBeforeCopy()
    {
        var script = UpdateService.BuildUpdateBatScript(1, "C:\\new.exe", "C:\\PrMonitor.exe");

        // Should move old exe to .old before copying
        Assert.Contains("move /y", script);
        Assert.Contains(".old", script);
    }

    [Fact]
    public void BuildUpdateBatScript_StartsNewExeAfterCopy()
    {
        var script = UpdateService.BuildUpdateBatScript(1, "C:\\new.exe", "C:\\PrMonitor.exe");

        // start "" launches the new exe
        Assert.Contains("start \"\"", script);
        Assert.Contains("C:\\PrMonitor.exe", script);
    }

    [Fact]
    public void BuildUpdateBatScript_SelfDeletes()
    {
        var script = UpdateService.BuildUpdateBatScript(1, "C:\\new.exe", "C:\\PrMonitor.exe");

        // The (goto) 2>nul & del "%~f0" idiom self-deletes the bat
        Assert.Contains("%~f0", script);
    }
}
