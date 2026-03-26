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
        var svc = new UpdateService(new DiagnosticsLogger());

        var result = svc.ParseReleaseResult(json, "1.0.0", "test");

        Assert.True(result.IsUpdateAvailable);
        Assert.Equal("2.0.0", result.LatestVersionText);
        Assert.Equal("https://github.com/owner/repo/releases/tag/v2.0.0", result.ReleaseUrl);
        Assert.Null(result.ErrorMessage);
    }

    [Fact]
    public void ParseReleaseResult_SameVersion_NoUpdateAvailable()
    {
        var json = """{"tag_name":"v1.6.1","html_url":"https://example.com/release"}""";
        var svc = new UpdateService(new DiagnosticsLogger());

        var result = svc.ParseReleaseResult(json, "1.6.1", "test");

        Assert.False(result.IsUpdateAvailable);
        Assert.Null(result.ErrorMessage);
    }

    [Fact]
    public void ParseReleaseResult_OlderRemoteVersion_NoUpdateAvailable()
    {
        var json = """{"tag_name":"v1.0.0","html_url":"https://example.com/release"}""";
        var svc = new UpdateService(new DiagnosticsLogger());

        var result = svc.ParseReleaseResult(json, "1.6.1", "test");

        Assert.False(result.IsUpdateAvailable);
    }

    [Fact]
    public void ParseReleaseResult_InvalidJson_ReturnsError()
    {
        var svc = new UpdateService(new DiagnosticsLogger());

        var result = svc.ParseReleaseResult("not-json", "1.0.0", "test");

        Assert.False(result.IsUpdateAvailable);
        Assert.NotNull(result.ErrorMessage);
    }

    [Fact]
    public void ParseReleaseResult_MissingRequiredFields_ReturnsError()
    {
        var json = """{"some_field":"value"}""";
        var svc = new UpdateService(new DiagnosticsLogger());

        var result = svc.ParseReleaseResult(json, "1.0.0", "test");

        Assert.False(result.IsUpdateAvailable);
        Assert.NotNull(result.ErrorMessage);
    }
}