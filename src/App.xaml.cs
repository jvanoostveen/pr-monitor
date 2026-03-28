using System.Threading;
using System.Windows;
using PrMonitor.Services;
using PrMonitor.Settings;
using PrMonitor.ViewModels;
using PrMonitor.Views;
using System.Diagnostics;

namespace PrMonitor;

/// <summary>
/// Application entry point. Wires services, tray icon, and windows.
/// Enforces single-instance via a named mutex.
/// </summary>
public partial class App : System.Windows.Application
{
    private Mutex? _singleInstanceMutex;
    private bool _ownsSingleInstanceMutex;
    private TrayIconManager? _trayIcon;
    private PollingService? _polling;
    private NotificationService? _notifications;
    private MainWindow? _mainWindow;
    private UpdateService? _updates;
    private DiagnosticsLogger? _logger;
    private MainViewModel? _mainVm;
    private System.Threading.Timer? _updateTimer;
    private GitHubService? _github;
    private FlakinessService? _flakinessService;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Enable native dark-mode menus on Windows 10 1903+ / Windows 11
        EnableDarkModeForNativeMenus();

        // ── Single-instance guard ──────────────────────────────────
        _singleInstanceMutex = new Mutex(true, "PrMonitor_SingleInstance", out var createdNew);
        _ownsSingleInstanceMutex = createdNew;
        if (!createdNew)
        {
            System.Windows.MessageBox.Show("PR Monitor is already running.", "PR Monitor",
                MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

        // ── Settings ───────────────────────────────────────────────
        var settings = AppSettings.Load();
        _logger = new DiagnosticsLogger();

        // Auto-detect username if not cached
        _github = new GitHubService(_logger);
        if (string.IsNullOrEmpty(settings.GitHubUsername))
        {
            settings.GitHubUsername = await _github.GetCurrentUserAsync();
            settings.Save();
        }

        // Apply auto-start registry setting
        SettingsViewModel.ApplyAutoStart(settings.AutoStartWithWindows);

        // Apply compact mode resources
        ApplyCompactMode(settings.CompactMode);

        // ── Services ───────────────────────────────────────────────
        _polling = new PollingService(_github, settings, _logger);

        _notifications = new NotificationService(settings);
        _notifications.Initialize();
        _notifications.Subscribe(_polling);
        _polling.MentionDetected += (_, title, repo) =>
            _notifications.Notify("You were mentioned", $"In {repo}: {title}");

        var copilot = new CopilotService(_logger);
        _flakinessService = new FlakinessService(_github, copilot, settings, _notifications, _logger);
        _flakinessService.Subscribe(_polling);

        _updates = new UpdateService(_logger);

        // ── View layer ─────────────────────────────────────────────
        var mainVm = new MainViewModel(settings, _notifications!);
        _mainVm = mainVm;
        mainVm.Subscribe(_polling);

        _mainWindow = new MainWindow(mainVm, settings, _github!, _notifications!, _logger);

        _trayIcon = new TrayIconManager(settings);
        _trayIcon.Subscribe(_polling);
        mainVm.OnHiddenPrsChanged = () => _trayIcon.RefreshFromLatestSnapshot();
        _trayIcon.OnOpenWindow(() =>
        {
            if (_mainWindow.IsVisible)
                _mainWindow.HideToTray();
            else
                _mainWindow.ShowAtTray();
        });
        _trayIcon.OnWindowVisibility(() => _mainWindow.IsVisible);
        _trayIcon.OnOpenSettings(() =>
        {
            var settingsVm = new SettingsViewModel(settings);
            var settingsWindow = new SettingsWindow(settingsVm, onSaved: () =>
            {
                // Apply new polling interval live and refresh data immediately
                _polling.UpdateInterval(settings.PollingIntervalSeconds);
                _ = _polling.RefreshAsync();
            });
            settingsWindow.ShowDialog();
        });
        _trayIcon.OnOpenAbout(() =>
        {
            var aboutWindow = new AboutWindow(
                TrayIconManager.GetAppVersion(),
                () => _ = CheckForUpdatesManuallyAsync());
            aboutWindow.ShowDialog();
        });
        _trayIcon.OnExit(() => Shutdown());

        // ── Start polling ──────────────────────────────────────────
        _polling.Start();

        if (settings.MainWindowVisible)
            _mainWindow.ShowAtTray();

        // Auto update check: first run after 30 s, then every 24 h
        _updateTimer = new System.Threading.Timer(_ => _ = RunAutoUpdateCheckAsync(), null,
            dueTime: TimeSpan.FromSeconds(30),
            period: TimeSpan.FromHours(24));
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _mainWindow?.PersistCurrentWindowState();
        _polling?.Dispose();
        _notifications?.Dispose();
        _trayIcon?.Dispose();
        _updateTimer?.Dispose();
        if (_singleInstanceMutex is not null)
        {
            // A second instance may never own the mutex; guard release to avoid shutdown-time crash.
            if (_ownsSingleInstanceMutex)
            {
                try
                {
                    _singleInstanceMutex.ReleaseMutex();
                }
                catch (ApplicationException ex)
                {
                    _logger?.Warn($"Mutex release skipped during shutdown because current thread did not own it. {DiagnosticsLogger.SummarizeException(ex)}");
                }
                catch (ObjectDisposedException ex)
                {
                    _logger?.Warn($"Mutex release skipped during shutdown because mutex was already disposed. {DiagnosticsLogger.SummarizeException(ex)}");
                }
            }

            _singleInstanceMutex.Dispose();
            _singleInstanceMutex = null;
            _ownsSingleInstanceMutex = false;
        }

        base.OnExit(e);
    }

    private async Task RunAutoUpdateCheckAsync()
    {
        if (_updates is null || _mainVm is null)
            return;

        var result = await _updates.CheckForUpdatesAsync();
        if (result.IsUpdateAvailable
            && !string.IsNullOrWhiteSpace(result.ReleaseUrl)
            && !string.IsNullOrWhiteSpace(result.LatestVersionText))
        {
            Dispatcher.Invoke(() => _mainVm.SetUpdateAvailable(result.LatestVersionText!, result.ReleaseUrl!));
        }
    }

    private async Task CheckForUpdatesManuallyAsync()
    {
        if (_updates is null)
            return;

        var result = await _updates.CheckForUpdatesAsync();

        if (!string.IsNullOrWhiteSpace(result.ErrorMessage))
        {
            _logger?.Warn($"Manual update check failed: {result.ErrorMessage}");
            System.Windows.MessageBox.Show(
                $"Unable to check for updates right now.\n\nDetails: {result.ErrorMessage}",
                "Check for updates",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        if (result.IsUpdateAvailable && !string.IsNullOrWhiteSpace(result.ReleaseUrl))
        {
            _mainVm?.SetUpdateAvailable(result.LatestVersionText!, result.ReleaseUrl!);

            var message =
                $"A new version of PR Monitor is available.\n\n" +
                $"Current version: {result.CurrentVersion}\n" +
                $"Latest version: {result.LatestVersionText}\n\n" +
                "Do you want to open the latest release page?";

            var answer = System.Windows.MessageBox.Show(
                message,
                "Update available",
                MessageBoxButton.YesNo,
                MessageBoxImage.Information);

            if (answer == MessageBoxResult.Yes)
                OpenInBrowser(result.ReleaseUrl);

            return;
        }

        System.Windows.MessageBox.Show(
            "You're using the latest version of PR Monitor.",
            "Check for updates",
            MessageBoxButton.OK,
            MessageBoxImage.Information);
    }

    private static void OpenInBrowser(string url)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps)
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
    }

    // Tells Windows to render native Win32 menus (and other controls) in
    // dark mode when the system is set to dark. Uses the undocumented but
    // widely-used uxtheme export #135 (SetPreferredAppMode).
    // Values: 0 = Default, 1 = AllowDark, 2 = ForceDark, 3 = ForceLight.
    [System.Runtime.InteropServices.DllImport("uxtheme.dll", EntryPoint = "#135")]
    private static extern int SetPreferredAppMode(int mode);

    private static void EnableDarkModeForNativeMenus()
    {
        try { SetPreferredAppMode(1 /* AllowDark */); }
        catch { /* non-critical: older Windows without uxtheme #135 */ }
    }

    public static void ApplyCompactMode(bool compact)
    {
        var resources = System.Windows.Application.Current.Resources;
        if (compact)
        {
            resources["PrRowPadding"] = new System.Windows.Thickness(8, 3, 8, 3);
            resources["PrRowFontSize"] = 11.5;
            resources["PrRowRepoFontSize"] = 9.5;
        }
        else
        {
            resources["PrRowPadding"] = new System.Windows.Thickness(8, 6, 8, 6);
            resources["PrRowFontSize"] = 12.0;
            resources["PrRowRepoFontSize"] = 10.0;
        }
    }
}

