namespace PrMonitor.Models;

public sealed class RerunRecord
{
    public int Count { get; set; }
    public DateTimeOffset LastAttempt { get; set; }
}
