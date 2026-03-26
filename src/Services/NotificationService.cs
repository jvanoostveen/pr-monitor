using System.Diagnostics;
using System.IO;
using Microsoft.Toolkit.Uwp.Notifications;
using PrMonitor.Models;
using PrMonitor.Settings;

namespace PrMonitor.Services;

/// <summary>
/// Sends Windows toast notifications for PR state changes.
/// Groups multiple changes of the same type from a single poll into one notification.
/// </summary>
public sealed class NotificationService : IDisposable
{
    private readonly AppSettings _settings;
    private bool _initialized;
    private bool _suppressInitialBatch = true;

    // Buffer of events collected within the current poll cycle.
    private readonly List<PrChangeEventArgs> _pending = [];

    public NotificationService(AppSettings settings)
    {
        _settings = settings;
    }

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
            var url = args.Argument;
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
        // Collect individual changes into the buffer.
        polling.PrChanged += (_, e) => _pending.Add(e);

        // After each poll, flush the buffer as grouped notifications.
        polling.Polled += (_, _) =>
        {
            if (_suppressInitialBatch)
            {
                _suppressInitialBatch = false;
                _pending.Clear();
                return;
            }

            FlushNotifications();
        };
    }

    public void Dispose()
    {
        try { ToastNotificationManagerCompat.History.Clear(); } catch { }
    }
    /// <summary>
    /// Show an ad-hoc toast notification directly (not tied to the polling event cycle).
    /// </summary>
    public void Notify(string title, string body)
    {
        if (!_initialized) return;
        if (_settings.NotificationMode == Models.NotificationMode.Never) return;
        if (_settings.NotificationMode == Models.NotificationMode.WhenWindowClosed && _settings.MainWindowVisible) return;
        ShowToast(title, body, "", "");
    }
    // ── Flush & grouping ────────────────────────────────────────────────

    private void FlushNotifications()
    {
        if (_pending.Count == 0) return;

        // Each notification "bucket" is identified by its header text.
        var groups = _pending
            .Select(e => (Header: GetHeader(e), e))
            .Where(x => x.Header is not null)
            .GroupBy(x => x.Header!);

        foreach (var group in groups)
        {
            if (!IsNotificationEnabled(group.Key)) continue;

            var items = group.Select(x => x.e).ToList();
            if (items.Count == 1)
            {
                var e = items[0];
                ShowToast(
                    group.Key,
                    $"{e.PullRequest.Repository}#{e.PullRequest.Number}" + (e.Kind == PrChangeKind.NewReviewRequested ? $" by {e.PullRequest.Author}" : ""),
                    e.PullRequest.Title,
                    e.PullRequest.Url);
            }
            else
            {
                // Summarise: "N pull requests" + individual titles (up to 4, then "…")
                var titles = items.Take(4).Select(e => $"• {e.PullRequest.Title}").ToList();
                if (items.Count > 4)
                    titles.Add($"… and {items.Count - 4} more");

                ShowToast(
                    group.Key,
                    $"{items.Count} pull requests",
                    string.Join("\n", titles),
                    items[0].PullRequest.Url); // open first PR on click
            }
        }

        _pending.Clear();
    }

    internal static string? GetHeader(PrChangeEventArgs e) => e.Kind switch
    {
        PrChangeKind.CIStatusChanged when e.PullRequest.CIState == CIState.Failure                                    => "❌ CI Failed",
        PrChangeKind.CIStatusChanged when e.PullRequest.CIState == CIState.Success && e.PreviousCIState == CIState.Failure => "✅ CI Passed",
        PrChangeKind.CIStatusChanged when e.PullRequest.CIState == CIState.Error                                      => "⚠️ CI Error",
        PrChangeKind.NewReviewRequested                                                                                => "👀 Review Requested",
        PrChangeKind.RemovedAutoMergePr                                                                                => "🔀 PR Merged / Closed",
        _ => null,
    };

    internal bool IsNotificationEnabled(string header)
    {
        if (_settings.NotificationMode == Models.NotificationMode.Never) return false;
        if (_settings.NotificationMode == Models.NotificationMode.WhenWindowClosed && _settings.MainWindowVisible) return false;

        return header switch
        {
            "❌ CI Failed"          => _settings.NotifyCiFailed,
            "✅ CI Passed"          => _settings.NotifyCiPassed,
            "⚠️ CI Error"          => _settings.NotifyCiError,
            "👀 Review Requested"  => _settings.NotifyReviewRequested,
            "🔀 PR Merged / Closed" => _settings.NotifyPrMergedOrClosed,
            _ => true,
        };
    }

    // ── Toast builder ───────────────────────────────────────────────────

    private static void ShowToast(string header, string line1, string line2, string url)
    {
        try
        {
            new ToastContentBuilder()
                .AddArgument(url)
                .AddText(header)
                .AddText(line1)
                .AddText(line2)
                .Show();
        }
        catch
        {
            // Swallow – toast infrastructure may not be available
        }
    }
}

