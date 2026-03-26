using PrMonitor.Services;
using Xunit;

namespace PrMonitor.Tests.Services;

public class FlakinessServiceTests
{
    [Fact]
    public void IsRegexMatch_MatchingInput_ReturnsTrue()
    {
        Assert.True(FlakinessService.IsRegexMatch(@"connection\s+timeout", "Error: connection timeout"));
    }

    [Fact]
    public void IsRegexMatch_NonMatchingInput_ReturnsFalse()
    {
        Assert.False(FlakinessService.IsRegexMatch("timeout", "All tests passed"));
    }

    [Fact]
    public void IsRegexMatch_InvalidPattern_ReturnsFalseWithoutThrowing()
    {
        var result = FlakinessService.IsRegexMatch("(unclosed", "some input");
        Assert.False(result);
    }

    [Theory]
    [InlineData(null, "input")]
    [InlineData("pattern", null)]
    [InlineData("", "input")]
    [InlineData("pattern", "")]
    [InlineData("   ", "input")]
    public void IsRegexMatch_NullOrEmptyArgs_ReturnsFalse(string? pattern, string? input)
    {
        Assert.False(FlakinessService.IsRegexMatch(pattern!, input!));
    }

    [Fact]
    public void IsRegexMatch_IsCaseInsensitive()
    {
        Assert.True(FlakinessService.IsRegexMatch("FLAKY", "this is a flaky test"));
    }

    [Fact]
    public void IsRegexMatch_MultilineMode_MatchesAcrossLines()
    {
        var log = "line1\nflaky\nline3";
        Assert.True(FlakinessService.IsRegexMatch("^flaky$", log));
    }

    [Fact]
    public void ExtractCheckNamesFromLog_EmptyLog_ReturnsEmpty()
    {
        Assert.Empty(FlakinessService.ExtractCheckNamesFromLog(""));
    }

    [Fact]
    public void ExtractCheckNamesFromLog_TabDelimitedLines_ExtractsFirstColumn()
    {
        var log = "Build\tStep 1\tsome output\nTest\tRun tests\tmore output";
        var result = FlakinessService.ExtractCheckNamesFromLog(log);
        Assert.Contains("Build", result);
        Assert.Contains("Test", result);
    }

    [Fact]
    public void ExtractCheckNamesFromLog_DuplicateJobNames_DeduplicatesResults()
    {
        var log = "Build\tStep 1\nBuild\tStep 2\nTest\tRun";
        var result = FlakinessService.ExtractCheckNamesFromLog(log);
        Assert.Equal(2, result.Count);
    }

    [Fact]
    public void ExtractCheckNamesFromLog_MoreThan10Jobs_CapsAt10()
    {
        var lines = Enumerable.Range(1, 15).Select(i => $"Job{i}\tstep");
        var log = string.Join("\n", lines);
        var result = FlakinessService.ExtractCheckNamesFromLog(log);
        Assert.Equal(10, result.Count);
    }

    [Fact]
    public void ExtractCheckNamesFromLog_NullInput_ReturnsEmpty()
    {
        Assert.Empty(FlakinessService.ExtractCheckNamesFromLog(null!));
    }
}