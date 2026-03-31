namespace PrMonitor.Models;

public sealed class FlakinessAnalysisResult
{
    public bool IsFlaky { get; init; }
    /// <summary>
    /// True when the analysis could not be completed (e.g. content filter blocked the request).
    /// When true, IsFlaky is meaningless and no action should be taken.
    /// </summary>
    public bool IsIndeterminate { get; init; }
    public string Rationale { get; init; } = "";
    public IReadOnlyList<FlakinessRuleSuggestion> SuggestedRules { get; init; } = [];
}

public sealed class FlakinessRuleSuggestion
{
    public required string Pattern { get; init; }
    public required string Description { get; init; }
}
