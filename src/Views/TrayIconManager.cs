using System.Diagnostics;
using System.Drawing;
using System.Reflection;
using System.Windows;
using Forms = System.Windows.Forms;
using PrMonitor.Models;
using PrMonitor.Services;
using PrMonitor.Settings;

namespace PrMonitor.Views;

/// <summary>
/// Manages the system tray (notification-area) icon, its context menu,
/// tooltip, and interaction with the floating window.
/// </summary>
public sealed class TrayIconManager : IDisposable
{
    private readonly Forms.NotifyIcon _notifyIcon;
    private readonly Forms.ContextMenuStrip _contextMenu;
    private readonly AppSettings _settings;

    // Menu items that get their text updated on poll
    private readonly Forms.ToolStripMenuItem _myPrsItem;
    private readonly Forms.ToolStripMenuItem _reviewsItem;
    private readonly Forms.ToolStripMenuItem _hotfixesItem;

    private Action? _openWindowAction;
    private Action? _openSettingsAction;
    private Action? _openAboutAction;
    private Action? _exitAction;

    public TrayIconManager(AppSettings settings)
    {
        _settings = settings;
        // ── Context menu ────────────────────────────────────────────
        _myPrsItem = new Forms.ToolStripMenuItem("My PRs (…)");
        _myPrsItem.Click += (_, _) => OpenInBrowser(
            "https://github.com/pulls?q=is%3Aopen+is%3Apr+author%3A%40me");

        _reviewsItem = new Forms.ToolStripMenuItem("Awaiting Review (…)");
        _reviewsItem.Click += (_, _) => OpenInBrowser(
            "https://github.com/pulls?q=is%3Aopen+is%3Apr+review-requested%3A%40me");

        _hotfixesItem = new Forms.ToolStripMenuItem("Hotfixes (…)");
        _hotfixesItem.Click += (_, _) => OpenInBrowser(
            "https://github.com/pulls?q=is%3Aopen+is%3Apr+involves%3A%40me+base%3Arelease");
        _hotfixesItem.Visible = false;

        var openItem = new Forms.ToolStripMenuItem("Open PR Monitor");
        openItem.Font = new Font(openItem.Font, System.Drawing.FontStyle.Bold);
        openItem.Click += (_, _) => _openWindowAction?.Invoke();

        var settingsItem = new Forms.ToolStripMenuItem("Settings…");
        settingsItem.Click += (_, _) => _openSettingsAction?.Invoke();

        var aboutItem = new Forms.ToolStripMenuItem("About…");
        aboutItem.Click += (_, _) => _openAboutAction?.Invoke();

        var versionItem = new Forms.ToolStripMenuItem($"Version {GetAppVersion()}")
        {
            Enabled = false,
        };

        var exitItem = new Forms.ToolStripMenuItem("Exit");
        exitItem.Click += (_, _) => _exitAction?.Invoke();

        _contextMenu = new Forms.ContextMenuStrip();
        _contextMenu.Items.AddRange([
            openItem,
            aboutItem,
            settingsItem,
            new Forms.ToolStripSeparator(),
            _hotfixesItem,
            _myPrsItem,
            _reviewsItem,
            new Forms.ToolStripSeparator(),
            versionItem,
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
    public void OnOpenAbout(Action action) => _openAboutAction = action;
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
        // Exclude hidden PRs from counts
        var hidden = _settings.HiddenPrKeys;
        int visibleAuto = snapshot.AutoMergePrs.Count(p => !hidden.Contains(p.Key));
        int visibleReview = snapshot.ReviewRequestedPrs.Count(p => !hidden.Contains(p.Key));
        int visibleHotfix = snapshot.HotfixPrs.Count(p => !hidden.Contains(p.Key));
        int totalVisible = visibleAuto + visibleReview + visibleHotfix;
        int failedCI = snapshot.AutoMergePrs.Count(p => !hidden.Contains(p.Key) && p.CIState == CIState.Failure)
                     + snapshot.HotfixPrs.Count(p => !hidden.Contains(p.Key) && p.CIState == CIState.Failure);
        bool hasLaterPrs = snapshot.AutoMergePrs.Any(p => hidden.Contains(p.Key))
                        || snapshot.ReviewRequestedPrs.Any(p => hidden.Contains(p.Key))
                        || snapshot.HotfixPrs.Any(p => hidden.Contains(p.Key));

        // Update icon
        var oldIcon = _notifyIcon.Icon;
        _notifyIcon.Icon = IconGenerator.CreateTrayIcon(totalVisible, failedCI, visibleReview, hasLaterPrs);
        oldIcon?.Dispose();

        // Tooltip
        var tooltipParts = new System.Text.StringBuilder("PR Monitor");
        if (visibleHotfix > 0) tooltipParts.Append($"\nHotfixes: {visibleHotfix}");
        tooltipParts.Append($"\nAuto-merge PRs: {visibleAuto}");
        tooltipParts.Append($"\nAwaiting review: {visibleReview}");
        var tooltip = tooltipParts.ToString();
        _notifyIcon.Text = tooltip.Length > 127 ? tooltip[..127] : tooltip;

        // Menu item labels
        _hotfixesItem.Text = $"Hotfixes ({visibleHotfix})";
        _hotfixesItem.Visible = visibleHotfix > 0;
        _myPrsItem.Text = $"My PRs ({visibleAuto})";
        _reviewsItem.Text = $"Awaiting Review ({visibleReview})";
    }

    // ── Helpers ─────────────────────────────────────────────────────

    private static void OpenInBrowser(string url)
    {
        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
    }

    public static string GetAppVersion()
    {
        var assembly = Assembly.GetEntryAssembly() ?? Assembly.GetExecutingAssembly();
        var informationalVersion = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion;

        if (!string.IsNullOrWhiteSpace(informationalVersion))
            return informationalVersion;

        return assembly.GetCustomAttribute<AssemblyFileVersionAttribute>()?.Version
            ?? assembly.GetName().Version?.ToString()
            ?? "unknown";
    }

    public void Dispose()
    {
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        _contextMenu.Dispose();
    }
}
