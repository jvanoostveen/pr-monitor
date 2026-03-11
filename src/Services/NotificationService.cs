using System.Diagnostics;
using System.IO;
using Microsoft.Toolkit.Uwp.Notifications;
using PrMonitor.Models;

namespace PrMonitor.Services;

/// <summary>
/// Sends Windows toast notifications for PR state changes.
/// Clicking a notification opens the PR in the default browser.
/// </summary>
public sealed class NotificationService : IDisposable
{
    private bool _initialized;
    private bool _suppressInitialBatch = true;

    /// <summary>
    /// Must be called once at startup to register the toast activator.
    /// </summary>
    public void Initialize()
    {
        if (_initialized) return;
        _initialized = true;

        // Remove any stale Start-menu shortcut created under the old exe name
        // so that the toolkit recreates it with the correct display name.
        var programsFolder = Environment.GetFolderPath(Environment.SpecialFolder.Programs);
        var stale = Path.Combine(programsFolder, "PrMonitor.lnk");
        if (File.Exists(stale))
            try { File.Delete(stale); } catch { }

        // Handle notification clicks → open URL in browser
        ToastNotificationManagerCompat.OnActivated += args =>
        {
            var url = args.Argument; // We pass the PR URL as the argument
            if (!string.IsNullOrEmpty(url) && Uri.TryCreate(url, UriKind.Absolute, out _))
            {
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            }
        };
    }

    /// <summary>
    /// Subscribe to a <see cref="PollingService"/> to send notifications on changes.
    /// </summary>
    public void Subscribe(PollingService polling)
    {
        polling.PrChanged += OnPrChanged;

        // After the first poll completes, stop suppressing notifications
        polling.Polled += (_, _) => _suppressInitialBatch = false;
    }

    public void Dispose()
    {
        // Clean up toast notification history on exit (optional)
        try { ToastNotificationManagerCompat.History.Clear(); } catch { }
    }

    // ── Event handler ───────────────────────────────────────────────────

    private void OnPrChanged(object? sender, PrChangeEventArgs e)
    {
        // Don't spam notifications on first load
        if (_suppressInitialBatch) return;

        switch (e.Kind)
        {
            case PrChangeKind.CIStatusChanged when e.PullRequest.CIState == CIState.Failure:
                ShowToast(
                    "❌ CI Failed",
                    $"{e.PullRequest.Repository}#{e.PullRequest.Number}",
                    e.PullRequest.Title,
                    e.PullRequest.Url);
                break;

            case PrChangeKind.CIStatusChanged when e.PullRequest.CIState == CIState.Success
                                                   && e.PreviousCIState == CIState.Failure:
                ShowToast(
                    "✅ CI Passed",
                    $"{e.PullRequest.Repository}#{e.PullRequest.Number}",
                    e.PullRequest.Title,
                    e.PullRequest.Url);
                break;

            case PrChangeKind.CIStatusChanged when e.PullRequest.CIState == CIState.Error:
                ShowToast(
                    "⚠️ CI Error",
                    $"{e.PullRequest.Repository}#{e.PullRequest.Number}",
                    e.PullRequest.Title,
                    e.PullRequest.Url);
                break;

            case PrChangeKind.NewReviewRequested:
                ShowToast(
                    "👀 Review Requested",
                    $"{e.PullRequest.Repository}#{e.PullRequest.Number} by {e.PullRequest.Author}",
                    e.PullRequest.Title,
                    e.PullRequest.Url);
                break;

            case PrChangeKind.RemovedAutoMergePr:
                ShowToast(
                    "🔀 PR Merged / Closed",
                    $"{e.PullRequest.Repository}#{e.PullRequest.Number}",
                    e.PullRequest.Title,
                    e.PullRequest.Url);
                break;
        }
    }

    // ── Toast builder ───────────────────────────────────────────────────

    private static void ShowToast(string header, string line1, string line2, string url)
    {
        try
        {
            new ToastContentBuilder()
                .AddArgument(url) // Passed to OnActivated as args.Argument
                .AddText(header)
                .AddText(line1)
                .AddText(line2)
                .Show();
        }
        catch
        {
            // Swallow – toast infrastructure may not be available (e.g. in tests)
        }
    }
}
