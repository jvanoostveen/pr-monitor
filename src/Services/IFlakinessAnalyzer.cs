using PrMonitor.Models;

namespace PrMonitor.Services;

/// <summary>An AI provider that classifies a CI failure as flaky or real.</summary>
public interface IFlakinessAnalyzer
{
    /// <summary>Short provider name for log lines.</summary>
    string Name { get; }

    /// <summary>Classifies the failure. Every error path returns an indeterminate result.</summary>
    Task<FlakinessAnalysisResult> AnalyzeFlakiness(FailureContext context, string? customHints = null);

    /// <summary>Checks with a minimal request whether the provider can be used right now.</summary>
    Task<AnalyzerCheckResult> CheckAsync();
}

public sealed record AnalyzerCheckResult(bool IsReady, string Message);
