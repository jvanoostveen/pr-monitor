using System.Text.Json;
using PrMonitor.Services;
using Xunit;

namespace PrMonitor.Tests.Services;

public class GitHubServiceRateLimitTests
{
    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement.Clone();

    [Theory]
    [InlineData("API rate limit exceeded for user ID 1234.")]
    [InlineData("You have exceeded a secondary rate limit and have been temporarily blocked.")]
    [InlineData("RATE_LIMITED")]
    [InlineData("rate_limited")]
    [InlineData("You have triggered an abuse detection mechanism.")]
    [InlineData("Retry-After: 60")]
    public void LooksRateLimited_RecognizesGitHubThrottlingMessages(string text)
    {
        Assert.True(GitHubService.LooksRateLimited(text));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Could not resolve to a Repository with the name 'o/r'.")]
    [InlineData("gh: Not Found (HTTP 404)")]
    public void LooksRateLimited_OtherFailures_AreNotMistakenForThrottling(string? text)
    {
        Assert.False(GitHubService.LooksRateLimited(text));
    }

    [Fact]
    public void HasRateLimitError_GraphQlRateLimitedType_IsDetected()
    {
        const string json = """
            { "errors": [ { "type": "RATE_LIMITED", "message": "API rate limit exceeded" } ] }
            """;

        Assert.True(GitHubService.HasRateLimitError(Parse(json)));
    }

    [Fact]
    public void HasRateLimitError_UnrelatedGraphQlError_IsNotDetected()
    {
        const string json = """
            { "errors": [ { "type": "NOT_FOUND", "message": "Could not resolve to a Repository" } ] }
            """;

        Assert.False(GitHubService.HasRateLimitError(Parse(json)));
    }

    [Fact]
    public void HasRateLimitError_ResponseWithoutErrors_IsNotDetected()
    {
        Assert.False(GitHubService.HasRateLimitError(Parse("""{ "data": { "rateLimit": { "remaining": 4999 } } }""")));
    }

    [Fact]
    public void ParseRateLimitBudget_ReadsRemainingAndResetAt()
    {
        const string json = """
            { "data": { "rateLimit": { "remaining": 4873, "resetAt": "2026-09-11T15:00:00Z" } } }
            """;

        var (remaining, resetAt) = GitHubService.ParseRateLimitBudget(Parse(json));

        Assert.Equal(4873, remaining);
        Assert.Equal(new DateTimeOffset(2026, 9, 11, 15, 0, 0, TimeSpan.Zero), resetAt);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("""{ "data": {} }""")]
    [InlineData("""{ "data": { "rateLimit": null } }""")]
    [InlineData("""{ "data": { "rateLimit": { "resetAt": "not-a-date" } } }""")]
    public void ParseRateLimitBudget_MissingOrUnusableBudget_ReturnsNulls(string json)
    {
        Assert.Equal((null, null), GitHubService.ParseRateLimitBudget(Parse(json)));
    }
}
