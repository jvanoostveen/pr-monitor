namespace PrMonitor.Models;

/// <summary>
/// Controls when Windows toast notifications are shown.
/// </summary>
public enum NotificationMode
{
    /// <summary>Always show toast notifications.</summary>
    Always,

    /// <summary>Only show toast notifications when the PR Monitor window is closed/hidden.</summary>
    WhenWindowClosed,

    /// <summary>Never show toast notifications.</summary>
    Never,
}
