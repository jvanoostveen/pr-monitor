using System.Diagnostics;
using System.Drawing;
using System.Windows;
using Forms = System.Windows.Forms;
using PrBot.Services;

namespace PrBot.Views;

/// <summary>
/// Manages the system tray (notification-area) icon, its context menu,
/// tooltip, and interaction with the floating window.
/// </summary>
public sealed class TrayIconManager : IDisposable
{
    private readonly Forms.NotifyIcon _notifyIcon;
    private readonly Forms.ContextMenuStrip _contextMenu;

    // Menu items that get their text updated on poll
    private readonly Forms.ToolStripMenuItem _myPrsItem;
    private readonly Forms.ToolStripMenuItem _reviewsItem;

    private Action? _openWindowAction;
    private Action? _openSettingsAction;
    private Action? _exitAction;

    public TrayIconManager()
    {
        // ── Context menu ────────────────────────────────────────────
        _myPrsItem = new Forms.ToolStripMenuItem("My PRs (…)");
        _myPrsItem.Click += (_, _) => OpenInBrowser(
            "https://github.com/pulls?q=is%3Aopen+is%3Apr+author%3A%40me");

        _reviewsItem = new Forms.ToolStripMenuItem("Awaiting Review (…)");
        _reviewsItem.Click += (_, _) => OpenInBrowser(
            "https://github.com/pulls?q=is%3Aopen+is%3Apr+review-requested%3A%40me");

        var openItem = new Forms.ToolStripMenuItem("Open PR Monitor");
        openItem.Font = new Font(openItem.Font, System.Drawing.FontStyle.Bold);
        openItem.Click += (_, _) => _openWindowAction?.Invoke();

        var settingsItem = new Forms.ToolStripMenuItem("Settings…");
        settingsItem.Click += (_, _) => _openSettingsAction?.Invoke();

        var exitItem = new Forms.ToolStripMenuItem("Exit");
        exitItem.Click += (_, _) => _exitAction?.Invoke();

        _contextMenu = new Forms.ContextMenuStrip();
        _contextMenu.Items.AddRange([
            openItem,
            new Forms.ToolStripSeparator(),
            _myPrsItem,
            _reviewsItem,
            new Forms.ToolStripSeparator(),
            settingsItem,
            new Forms.ToolStripSeparator(),
            exitItem,
        ]);

        // ── Notify icon ─────────────────────────────────────────────
        _notifyIcon = new Forms.NotifyIcon
        {
            Icon = IconGenerator.CreateTrayIcon(0, 0, 0),
            Text = "PR Monitor – loading…",
            Visible = true,
            ContextMenuStrip = _contextMenu,
        };

        // Single left-click toggles the window
        _notifyIcon.MouseClick += (_, e) =>
        {
            if (e.Button == Forms.MouseButtons.Left)
                _openWindowAction?.Invoke();
        };
    }

    // ── Configuration ───────────────────────────────────────────────

    public void OnOpenWindow(Action action) => _openWindowAction = action;
    public void OnOpenSettings(Action action) => _openSettingsAction = action;
    public void OnExit(Action action) => _exitAction = action;

    // ── Subscribe to polling ────────────────────────────────────────

    public void Subscribe(PollingService polling)
    {
        polling.Polled += (_, snapshot) =>
        {
            // NotifyIcon must be updated on the thread that created it,
            // but timer callbacks come from a threadpool thread.
            System.Windows.Application.Current?.Dispatcher.Invoke(() =>
                UpdateFromSnapshot(snapshot));
        };
    }

    // ── Update from poll data ───────────────────────────────────────

    private void UpdateFromSnapshot(PollSnapshot snapshot)
    {
        // Update icon
        var oldIcon = _notifyIcon.Icon;
        _notifyIcon.Icon = IconGenerator.CreateTrayIcon(
            snapshot.TotalCount,
            snapshot.FailedCICount,
            snapshot.ReviewRequestedPrs.Count);
        oldIcon?.Dispose();

        // Tooltip
        var tooltip = $"PR Monitor\n" +
                      $"Auto-merge PRs: {snapshot.AutoMergePrs.Count}\n" +
                      $"Awaiting review: {snapshot.ReviewRequestedPrs.Count}";
        // NotifyIcon.Text max length is 127 chars; truncate if needed
        _notifyIcon.Text = tooltip.Length > 127 ? tooltip[..127] : tooltip;

        // Menu item labels
        _myPrsItem.Text = $"My PRs ({snapshot.AutoMergePrs.Count})";
        _reviewsItem.Text = $"Awaiting Review ({snapshot.ReviewRequestedPrs.Count})";
    }

    // ── Helpers ─────────────────────────────────────────────────────

    private static void OpenInBrowser(string url)
    {
        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
    }

    public void Dispose()
    {
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        _contextMenu.Dispose();
    }
}
