namespace PrMonitor.Models;

/// <summary>
/// Latest review state of a single reviewer on a pull request.
/// Mirrors GitHub's PullRequestReviewState enum (DISMISSED and draft PENDING reviews are excluded upstream).
/// </summary>
public enum ReviewState
{
    /// <summary>No review submitted yet; a review request is currently pending.</summary>
    Pending,

    /// <summary>Reviewer left comments without approving or requesting changes.</summary>
    Commented,

    /// <summary>Reviewer approved the pull request.</summary>
    Approved,

    /// <summary>Reviewer requested changes.</summary>
    ChangesRequested,
}

/// <summary>
/// Display helpers for <see cref="ReviewState"/>.
/// </summary>
public static class ReviewStateExtensions
{
    /// <summary>Human-readable label for a <see cref="ReviewState"/>, used in the UI (context menu, tooltips).</summary>
    public static string ToDisplayString(this ReviewState state) => state switch
    {
        ReviewState.Approved => "Approved",
        ReviewState.ChangesRequested => "Changes requested",
        ReviewState.Commented => "Commented",
        _ => "Pending",
    };
}
