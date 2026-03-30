using PrMonitor.Services;
using Xunit;

namespace PrMonitor.Tests.Services;

public class DiagnosticsLoggerTests
{
    [Fact]
    public void SummarizeException_BasicException_IncludesTypeAndMessage()
    {
        var ex = new InvalidOperationException("Something went wrong");
        var result = DiagnosticsLogger.SummarizeException(ex);

        Assert.StartsWith("InvalidOperationException: Something went wrong", result);
    }

    [Fact]
    public void SummarizeException_ExceptionWithStackTrace_IncludesStackLabel()
    {
        Exception ex;
        try { throw new InvalidOperationException("boom"); }
        catch (Exception caught) { ex = caught; }

        var result = DiagnosticsLogger.SummarizeException(ex);

        Assert.Contains("| Stack:", result);
    }

    [Fact]
    public void SummarizeException_LongStackTrace_IncludesAtMostFourFrames()
    {
        // Produce a deep call stack by recurring through a helper
        Exception ex;
        try { DeepThrow(10); ex = null!; }
        catch (Exception caught) { ex = caught; }

        var result = DiagnosticsLogger.SummarizeException(ex);

        // Frames are separated by " <= "; so at most 4 frames means at most 3 separators
        var separatorCount = result.Split(" <= ").Length - 1;
        Assert.True(separatorCount <= 3, $"Expected at most 3 separators but got {separatorCount}");
    }

    [Fact]
    public void SummarizeException_ExceptionNoStackTrace_NoStackSection()
    {
        // Create an exception without a stack trace (not thrown)
        var ex = new ArgumentNullException("param", "Param was null");
        var result = DiagnosticsLogger.SummarizeException(ex);

        Assert.DoesNotContain("| Stack:", result);
    }

    [Fact]
    public void SummarizeException_DifferentExceptionTypes_StartsWithTypeName()
    {
        var ex = new ArgumentException("bad argument");
        var result = DiagnosticsLogger.SummarizeException(ex);

        Assert.StartsWith("ArgumentException:", result);
    }

    private static void DeepThrow(int depth)
    {
        if (depth == 0) throw new InvalidOperationException("deep");
        DeepThrow(depth - 1);
    }
}
