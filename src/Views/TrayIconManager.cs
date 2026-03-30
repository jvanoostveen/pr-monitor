using System.Diagnostics;
using System.Drawing;
using System.Reflection;
using System.Runtime.InteropServices;
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
    private readonly Forms.Form _menuOwner;   // hidden HWND owner for TrackPopupMenuEx
    private readonly AppSettings _settings;

    // Dynamic text for native menu items, updated on each poll
    private string _myPrsText      = "My PRs (…)";
    private string _reviewsText    = "Awaiting Review (…)";
    private string _hotfixesText   = "Hotfixes (…)";
    private bool   _hotfixesVisible;

    private Action? _openWindowAction;
    private Action? _openSettingsAction;
    private Action? _openAboutAction;
    private Action? _exitAction;
    private Func<bool>? _isWindowVisible;

    // ── Win32 native popup menu ──────────────────────────────────
    [DllImport("user32.dll")] static extern IntPtr CreatePopupMenu();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern bool AppendMenuW(IntPtr hMenu, uint uFlags, UIntPtr uIDNewItem, string? lpNewItem);
    [DllImport("user32.dll")] static extern uint TrackPopupMenuEx(IntPtr hmenu, uint fuFlags, int x, int y, IntPtr hwnd, IntPtr lptpm);
    [DllImport("user32.dll")] static extern bool DestroyMenu(IntPtr hMenu);
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr hWnd);

    private const uint MF_STRING       = 0x0000;
    private const uint MF_SEPARATOR    = 0x0800;
    private const uint MF_GRAYED       = 0x0001;
    private const uint MF_DEFAULT      = 0x1000;
    private const uint TPM_BOTTOMALIGN = 0x0020;
    private const uint TPM_RETURNCMD   = 0x0100;
    private const uint TPM_RIGHTBUTTON = 0x0002;

    private const uint ID_OPEN     = 1;
    private const uint ID_ABOUT    = 2;
    private const uint ID_SETTINGS = 3;
    private const uint ID_HOTFIXES = 4;
    private const uint ID_MY_PRS   = 5;
    private const uint ID_REVIEWS  = 6;
    private const uint ID_EXIT     = 7;

    public TrayIconManager(AppSettings settings)
    {
        _settings = settings;

        // Invisible owner window so TrackPopupMenuEx always has a valid HWND,
        // even before the main WPF window has ever been shown.
        _menuOwner = new Forms.Form
        {
            Width = 0, Height = 0,
            ShowInTaskbar = false,
            Opacity = 0,
            FormBorderStyle = Forms.FormBorderStyle.None,
        };
        _menuOwner.Show();
        _menuOwner.Hide();

        // ── Notify icon ─────────────────────────────────────────────
        _notifyIcon = new Forms.NotifyIcon
        {
            Icon = IconGenerator.CreateTrayIcon(0, 0, 0),
            Text = "PR Monitor – loading…",
            Visible = true,
        };

        _notifyIcon.MouseClick += (_, e) =>
        {
            if (e.Button == Forms.MouseButtons.Left)
                _openWindowAction?.Invoke();
            else if (e.Button == Forms.MouseButtons.Right)
                ShowNativeContextMenu();
        };
    }

    // ── Configuration ───────────────────────────────────────────────

    public void OnOpenWindow(Action action) => _openWindowAction = action;
    public void OnWindowVisibility(Func<bool> isVisible) => _isWindowVisible = isVisible;
    public void OnOpenSettings(Action action) => _openSettingsAction = action;
    public void OnOpenAbout(Action action) => _openAboutAction = action;
    public void OnExit(Action action) => _exitAction = action;

    // ── Native context menu ─────────────────────────────────────────

    private void ShowNativeContextMenu()
    {
        var hMenu = CreatePopupMenu();
        if (hMenu == IntPtr.Zero) return;
        try
        {
            var openLabel = _isWindowVisible?.Invoke() == true ? "Close PR Monitor" : "Open PR Monitor";
            AppendMenuW(hMenu, MF_STRING | MF_DEFAULT, (UIntPtr)ID_OPEN,     openLabel);
            AppendMenuW(hMenu, MF_STRING,               (UIntPtr)ID_ABOUT,    "About…");
            AppendMenuW(hMenu, MF_STRING,               (UIntPtr)ID_SETTINGS, "Settings…");
            AppendMenuW(hMenu, MF_SEPARATOR,             UIntPtr.Zero,         null);
            if (_hotfixesVisible)
                AppendMenuW(hMenu, MF_STRING, (UIntPtr)ID_HOTFIXES, _hotfixesText);
            AppendMenuW(hMenu, MF_STRING, (UIntPtr)ID_MY_PRS,  _myPrsText);
            AppendMenuW(hMenu, MF_STRING, (UIntPtr)ID_REVIEWS, _reviewsText);
            AppendMenuW(hMenu, MF_SEPARATOR, UIntPtr.Zero, null);
            AppendMenuW(hMenu, MF_STRING, (UIntPtr)ID_EXIT, "Exit");

            var pt   = Forms.Cursor.Position;
            var hwnd = _menuOwner.Handle;
            SetForegroundWindow(hwnd);

            uint cmd = TrackPopupMenuEx(
                hMenu,
                TPM_BOTTOMALIGN | TPM_RETURNCMD | TPM_RIGHTBUTTON,
                pt.X, pt.Y, hwnd, IntPtr.Zero);

            switch (cmd)
            {
                case ID_OPEN:     _openWindowAction?.Invoke();  break;
                case ID_ABOUT:    _openAboutAction?.Invoke();   break;
                case ID_SETTINGS: _openSettingsAction?.Invoke(); break;
                case ID_HOTFIXES: OpenInBrowser("https://github.com/pulls?q=is%3Aopen+is%3Apr+involves%3A%40me+base%3Arelease"); break;
                case ID_MY_PRS:   OpenInBrowser("https://github.com/pulls?q=is%3Aopen+is%3Apr+author%3A%40me"); break;
                case ID_REVIEWS:  OpenInBrowser("https://github.com/pulls?q=is%3Aopen+is%3Apr+review-requested%3A%40me"); break;
                case ID_EXIT:     _exitAction?.Invoke(); break;
            }
        }
        finally
        {
            DestroyMenu(hMenu);
        }
    }

    // ── Subscribe to polling ────────────────────────────────────────

    private PollSnapshot? _latestSnapshot;

    public void Subscribe(PollingService polling)
    {
        polling.Polled += (_, snapshot) =>
        {
            // NotifyIcon must be updated on the thread that created it,
            // but timer callbacks come from a threadpool thread.
            System.Windows.Application.Current?.Dispatcher.Invoke(() =>
            {
                _latestSnapshot = snapshot;
                UpdateFromSnapshot(snapshot);
            });
        };
    }

    /// <summary>
    /// Re-evaluate icon/tooltip/counts from the last known snapshot.
    /// Call this whenever the hidden-PR set changes without a new poll.
    /// </summary>
    public void RefreshFromLatestSnapshot()
    {
        if (_latestSnapshot is { } snapshot)
            UpdateFromSnapshot(snapshot);
    }

    // ── Update from poll data ───────────────────────────────────────

    private void UpdateFromSnapshot(PollSnapshot snapshot)
    {
        // Exclude hidden PRs from counts
        var hidden = _settings.HiddenPrKeys;
        int visibleAuto       = snapshot.AutoMergePrs.Count(p => !hidden.Contains(p.Key));
        int visibleMyPrs      = snapshot.MyPrs.Count(p => !hidden.Contains(p.Key));
        int visibleReview     = snapshot.ReviewRequestedPrs.Count(p => !hidden.Contains(p.Key));
        int visibleTeamReview = snapshot.TeamReviewRequestedPrs.Count(p => !hidden.Contains(p.Key));
        int visibleHotfix     = snapshot.HotfixPrs.Count(p => !hidden.Contains(p.Key));
        int totalVisible      = visibleAuto + visibleMyPrs + visibleReview
                              + (_settings.TeamReviewCountsForTrayIcon ? visibleTeamReview : 0)
                              + visibleHotfix;

        // Red: CI failures across auto-merge, hotfix and non-draft My PRs
        int failedCI = snapshot.AutoMergePrs.Count(p => !hidden.Contains(p.Key) && p.CIState == CIState.Failure)
                     + snapshot.HotfixPrs.Count(p => !hidden.Contains(p.Key) && p.CIState == CIState.Failure)
                     + snapshot.MyPrs.Count(p => !hidden.Contains(p.Key) && !p.IsDraft && p.CIState == CIState.Failure);

        // Amber: reviews requested on me + unresolved comments on My PRs
        int unresolvedOnMyPrs = snapshot.MyPrs.Count(p => !hidden.Contains(p.Key) && p.UnresolvedReviewCommentCount > 0);
        int amberCount = visibleReview
                       + (_settings.TeamReviewCountsForTrayIcon ? visibleTeamReview : 0)
                       + unresolvedOnMyPrs;

        // Purple: pipeline still running (pending CI on non-draft, non-hidden PRs)
        int pendingCI = snapshot.AutoMergePrs.Count(p => !hidden.Contains(p.Key) && p.CIState == CIState.Pending)
                      + snapshot.HotfixPrs.Count(p => !hidden.Contains(p.Key) && p.CIState == CIState.Pending)
                      + snapshot.MyPrs.Count(p => !hidden.Contains(p.Key) && !p.IsDraft && p.CIState == CIState.Pending);

        bool hasLaterPrs = snapshot.AutoMergePrs.Any(p => hidden.Contains(p.Key))
                        || snapshot.MyPrs.Any(p => hidden.Contains(p.Key))
                        || snapshot.ReviewRequestedPrs.Any(p => hidden.Contains(p.Key))
                        || snapshot.TeamReviewRequestedPrs.Any(p => hidden.Contains(p.Key))
                        || snapshot.HotfixPrs.Any(p => hidden.Contains(p.Key));

        // Update icon
        var oldIcon = _notifyIcon.Icon;
        _notifyIcon.Icon = IconGenerator.CreateTrayIcon(totalVisible, failedCI, amberCount, pendingCI, hasLaterPrs);
        oldIcon?.Dispose();

        // Tooltip
        var tooltipParts = new System.Text.StringBuilder("PR Monitor");
        if (visibleHotfix > 0) tooltipParts.Append($"\nHotfixes: {visibleHotfix}");
        tooltipParts.Append($"\nAuto-merge PRs: {visibleAuto}");
        if (visibleMyPrs > 0) tooltipParts.Append($"\nMy PRs: {visibleMyPrs}");
        tooltipParts.Append($"\nAwaiting review: {visibleReview}");
        if (visibleTeamReview > 0) tooltipParts.Append($"\nTeam review: {visibleTeamReview}");
        var tooltip = tooltipParts.ToString();
        _notifyIcon.Text = tooltip.Length > 127 ? tooltip[..127] : tooltip;

        // Track menu item labels (shown in native menu on next right-click)
        _hotfixesText    = $"Hotfixes ({visibleHotfix})";
        _hotfixesVisible = visibleHotfix > 0;
        _myPrsText       = $"My PRs ({visibleAuto + visibleMyPrs})";
        _reviewsText     = $"Awaiting Review ({visibleReview})";
    }

    // ── Helpers ─────────────────────────────────────────────────────

    private static void OpenInBrowser(string url)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps)
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
        _menuOwner.Dispose();
    }

}

