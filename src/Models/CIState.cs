namespace PrMonitor.Models;

/// <summary>
/// Aggregate CI / check-run state for a pull request.
/// Mirrors GitHub's StatusState enum.
/// </summary>
public enum CIState
{
    Unknown,
    Pending,
    Success,
    Failure,
    Error,
}
