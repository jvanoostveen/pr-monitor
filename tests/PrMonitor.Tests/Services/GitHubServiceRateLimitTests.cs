using System.Text.Json;
using PrMonitor.Models;
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

    // ── Shared snapshot, filled by every query including the poll ───────

    [Fact]
    public void ParseRateLimitSnapshot_ReadsTheWholeBudget()
    {
        const string json = """
            { "data": { "rateLimit": { "limit": 5000, "remaining": 4873, "resetAt": "2026-09-11T15:00:00Z" } } }
            """;
        var observed = new DateTimeOffset(2026, 9, 11, 14, 30, 0, TimeSpan.Zero);

        var snapshot = GitHubService.ParseRateLimitSnapshot(Parse(json), observed);

        Assert.NotNull(snapshot);
        Assert.Equal(4873, snapshot!.Remaining);
        Assert.Equal(5000, snapshot.Limit);
        Assert.Equal(new DateTimeOffset(2026, 9, 11, 15, 0, 0, TimeSpan.Zero), snapshot.ResetAt);
        Assert.Equal(observed, snapshot.ObservedAt);
    }

    [Fact]
    public void ParseRateLimitSnapshot_WithoutTheField_ReturnsNull()
    {
        Assert.Null(GitHubService.ParseRateLimitSnapshot(Parse("""{ "data": { "search": { "nodes": [] } } }"""), DateTimeOffset.UtcNow));
    }

    [Fact]
    public void ParseRateLimitSnapshot_MissingLimit_StillReportsRemaining()
    {
        var snapshot = GitHubService.ParseRateLimitSnapshot(
            Parse("""{ "data": { "rateLimit": { "remaining": 42 } } }"""), DateTimeOffset.UtcNow);

        Assert.NotNull(snapshot);
        Assert.Equal(42, snapshot!.Remaining);
        Assert.Equal(0, snapshot.Limit);
        Assert.Equal(1, snapshot.RemainingFraction);
    }

    [Fact]
    public void Snapshot_RemainingFraction_ReflectsHowMuchOfTheWindowIsLeft()
    {
        var snapshot = new RateLimitSnapshot(1250, 5000, null, DateTimeOffset.UtcNow);

        Assert.Equal(0.25, snapshot.RemainingFraction);
    }

    [Fact]
    public void Snapshot_IsStale_OnceItsOwnResetTimeHasPassed()
    {
        var now = new DateTimeOffset(2026, 9, 11, 14, 0, 0, TimeSpan.Zero);
        var fresh = new RateLimitSnapshot(100, 5000, now.AddMinutes(10), now);
        var expired = new RateLimitSnapshot(100, 5000, now.AddMinutes(-10), now.AddMinutes(-40));

        Assert.False(fresh.IsStale(now));
        Assert.True(expired.IsStale(now));
    }

    [Fact]
    public void Snapshot_TimeUntilReset_IsZeroWhenUnknownOrPast()
    {
        var now = new DateTimeOffset(2026, 9, 11, 14, 0, 0, TimeSpan.Zero);

        Assert.Equal(TimeSpan.Zero, new RateLimitSnapshot(1, 5000, null, now).TimeUntilReset(now));
        Assert.Equal(TimeSpan.Zero, new RateLimitSnapshot(1, 5000, now.AddMinutes(-1), now).TimeUntilReset(now));
        Assert.Equal(TimeSpan.FromMinutes(15), new RateLimitSnapshot(1, 5000, now.AddMinutes(15), now).TimeUntilReset(now));
    }
}
