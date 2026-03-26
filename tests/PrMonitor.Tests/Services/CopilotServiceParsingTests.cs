using System.Text.Json;
using PrMonitor.Services;
using Xunit;

namespace PrMonitor.Tests.Services;

public class CopilotServiceParsingTests
{
    private readonly CopilotService _svc = new(new DiagnosticsLogger());

    [Fact]
    public void ParseResponse_FlakyResult_ReturnsIsFlakyTrue()
    {
        var inner = """{"isFlaky":true,"rationale":"Browser test flakiness","suggestedRules":[]}""";
        var response = BuildChatResponse(inner);

        var result = _svc.ParseResponse(response);

        Assert.True(result.IsFlaky);
        Assert.Equal("Browser test flakiness", result.Rationale);
        Assert.Empty(result.SuggestedRules);
    }

    [Fact]
    public void ParseResponse_NotFlakyWithSuggestedRules_ParsesRulesCorrectly()
    {
        var inner = """{"isFlaky":false,"rationale":"Actual test failure","suggestedRules":[{"pattern":"connection timeout","description":"Network timeout"},{"pattern":"socket hang up","description":"Socket error"}]}""";
        var response = BuildChatResponse(inner);

        var result = _svc.ParseResponse(response);

        Assert.False(result.IsFlaky);
        Assert.Equal(2, result.SuggestedRules.Count);
        Assert.Equal("connection timeout", result.SuggestedRules[0].Pattern);
        Assert.Equal("Network timeout",    result.SuggestedRules[0].Description);
    }

    [Fact]
    public void ParseResponse_ContentInMarkdownFences_ParsesCorrectly()
    {
        var inner = """{"isFlaky":true,"rationale":"flaky","suggestedRules":[]}""";
        var fenced = $"```json\n{inner}\n```";
        var response = BuildChatResponse(fenced);

        var result = _svc.ParseResponse(response);

        Assert.True(result.IsFlaky);
    }

    [Fact]
    public void ParseResponse_InvalidInnerJson_ReturnsFallback()
    {
        var response = BuildChatResponse("this is not json");

        var result = _svc.ParseResponse(response);

        Assert.False(result.IsFlaky);
        Assert.NotEmpty(result.Rationale);
    }

    [Fact]
    public void ParseResponse_MalformedOuterJson_ReturnsFallback()
    {
        var result = _svc.ParseResponse("totally not json");

        Assert.False(result.IsFlaky);
        Assert.NotEmpty(result.Rationale);
    }

    private static string BuildChatResponse(string content)
    {
        var jsonContent = JsonSerializer.Serialize(content);
        return "{\"choices\":[{\"message\":{\"content\":" + jsonContent + "}}]}";
    }
}