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
    private TrayIconManager? _trayIcon;
    private PollingService? _polling;
    private NotificationService? _notifications;
    private MainWindow? _mainWindow;
    private UpdateService? _updates;
    private DiagnosticsLogger? _logger;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // ── Single-instance guard ──────────────────────────────────
        _singleInstanceMutex = new Mutex(true, "PrMonitor_SingleInstance", out var createdNew);
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
        var github = new GitHubService(_logger);
        if (string.IsNullOrEmpty(settings.GitHubUsername))
        {
            settings.GitHubUsername = await github.GetCurrentUserAsync();
            settings.Save();
        }

        // Apply auto-start registry setting
        SettingsViewModel.ApplyAutoStart(settings.AutoStartWithWindows);

        // ── Services ───────────────────────────────────────────────
        _polling = new PollingService(github, settings, _logger);

        _notifications = new NotificationService();
        _notifications.Initialize();
        _notifications.Subscribe(_polling);
        _updates = new UpdateService();

        // ── View layer ─────────────────────────────────────────────
        var mainVm = new MainViewModel(settings);
        mainVm.Subscribe(_polling);

        _mainWindow = new MainWindow(mainVm);

        _trayIcon = new TrayIconManager(settings);
        _trayIcon.Subscribe(_polling);
        _trayIcon.OnOpenWindow(() =>
        {
            if (_mainWindow.IsVisible)
                _mainWindow.Hide();
            else
                _mainWindow.ShowAtTray();
        });
        _trayIcon.OnOpenSettings(() =>
        {
            var settingsVm = new SettingsViewModel(settings);
            var settingsWindow = new SettingsWindow(settingsVm, onSaved: () =>
            {
                // Apply new polling interval live
                _polling.UpdateInterval(settings.PollingIntervalSeconds);
            });
            settingsWindow.ShowDialog();
        });
        _trayIcon.OnCheckForUpdates(() => _ = CheckForUpdatesManuallyAsync());
        _trayIcon.OnExit(() => Shutdown());

        // ── Start polling ──────────────────────────────────────────
        _polling.Start();

        // Non-blocking startup update check
        _ = CheckForUpdatesOnStartupAsync();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _polling?.Dispose();
        _notifications?.Dispose();
        _trayIcon?.Dispose();
        _singleInstanceMutex?.ReleaseMutex();
        _singleInstanceMutex?.Dispose();

        base.OnExit(e);
    }

    private async Task CheckForUpdatesOnStartupAsync()
    {
        if (_updates is null)
            return;

        var result = await _updates.CheckForUpdatesAsync();
        if (!result.IsUpdateAvailable || string.IsNullOrWhiteSpace(result.ReleaseUrl))
            return;

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
    }

    private async Task CheckForUpdatesManuallyAsync()
    {
        if (_updates is null)
            return;

        var result = await _updates.CheckForUpdatesAsync();

        if (!string.IsNullOrWhiteSpace(result.ErrorMessage))
        {
            System.Windows.MessageBox.Show(
                "Unable to check for updates right now. Please try again later.",
                "Check for updates",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        if (result.IsUpdateAvailable && !string.IsNullOrWhiteSpace(result.ReleaseUrl))
        {
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
        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
    }
}

