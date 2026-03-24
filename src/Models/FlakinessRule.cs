namespace PrMonitor.Models;

public sealed class FlakinessRule
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    /// <summary>Regex pattern matched against the log excerpt.</summary>
    public required string Pattern { get; set; }
    public required string Description { get; set; }
    public bool IsEnabled { get; set; } = true;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public int MatchCount { get; set; } = 0;
}
