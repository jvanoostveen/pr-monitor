namespace PrMonitor.Models;

/// <summary>
/// What GitHub last reported about the GraphQL rate-limit budget. Every query asks for
/// <c>rateLimit</c> (which costs no points), so polling keeps this current and the checks
/// panel can pace itself against a budget it did not have to spend a call to learn.
/// </summary>
/// <param name="Remaining">Points left in the current window.</param>
/// <param name="Limit">Total points in the window, or 0 when GitHub did not report it.</param>
/// <param name="ResetAt">When the window resets.</param>
/// <param name="ObservedAt">When this snapshot was taken, so staleness can be judged.</param>
public sealed record RateLimitSnapshot(
    int Remaining,
    int Limit,
    DateTimeOffset? ResetAt,
    DateTimeOffset ObservedAt)
{
    /// <summary>Fraction of the window still available, or 1 when the limit is unknown.</summary>
    public double RemainingFraction => Limit > 0 ? (double)Remaining / Limit : 1;

    /// <summary>Time left before the budget replenishes; zero when unknown or already past.</summary>
    public TimeSpan TimeUntilReset(DateTimeOffset now) =>
        ResetAt is { } reset && reset > now ? reset - now : TimeSpan.Zero;

    /// <summary>
    /// A snapshot older than the reset it predicted no longer describes the current window.
    /// </summary>
    public bool IsStale(DateTimeOffset now) => ResetAt is { } reset && reset <= now;

    public override string ToString() =>
        $"{Remaining}{(Limit > 0 ? $"/{Limit}" : "")} points left" +
        (ResetAt is { } reset ? $", resets {reset.ToLocalTime():HH:mm}" : "");
}
