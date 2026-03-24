namespace PrMonitor.Models;

public sealed class FlakinessAnalysisResult
{
    public bool IsFlaky { get; init; }
    public string Rationale { get; init; } = "";
    public IReadOnlyList<FlakinessRuleSuggestion> SuggestedRules { get; init; } = [];
}

public sealed class FlakinessRuleSuggestion
{
    public required string Pattern { get; init; }
    public required string Description { get; init; }
}
