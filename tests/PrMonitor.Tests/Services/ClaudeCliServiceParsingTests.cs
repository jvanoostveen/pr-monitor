using System.Text.Json;
using PrMonitor.Services;
using Xunit;

namespace PrMonitor.Tests.Services;

public class ClaudeCliServiceParsingTests
{
    private readonly ClaudeCliService _svc = new(DiagnosticsLogger.Null);

    [Fact]
    public void ParseCliOutput_SuccessfulRun_ParsesVerdictFromResult()
    {
        var verdict = """{"isFlaky":true,"rationale":"Network reset in E2E test","suggestedRules":[]}""";

        var result = _svc.ParseCliOutput(BuildCliOutput(isError: false, verdict));

        Assert.False(result.IsIndeterminate);
        Assert.True(result.IsFlaky);
        Assert.Equal("Network reset in E2E test", result.Rationale);
    }

    [Fact]
    public void ParseCliOutput_ErrorRun_ReturnsIndeterminateWithTheError()
    {
        var output = BuildCliOutput(isError: true, "Failed to authenticate: OAuth session expired");

        var result = _svc.ParseCliOutput(output);

        Assert.True(result.IsIndeterminate);
        Assert.False(result.IsFlaky);
        Assert.Contains("OAuth session expired", result.Rationale);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("""{"type":"result"}""")]
    public void ParseCliOutput_UnusableOutput_ReturnsIndeterminate(string stdout)
    {
        var result = _svc.ParseCliOutput(stdout);

        Assert.True(result.IsIndeterminate);
        Assert.False(result.IsFlaky);
    }

    private static string BuildCliOutput(bool isError, string result) =>
        JsonSerializer.Serialize(new { type = "result", is_error = isError, result });
}
