using System.Threading;
using System.Windows;
using PrMonitor.Services;
using PrMonitor.Settings;
using PrMonitor.ViewModels;
using PrMonitor.Views;

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

        // Auto-detect username if not cached
        var github = new GitHubService();
        if (string.IsNullOrEmpty(settings.GitHubUsername))
        {
            settings.GitHubUsername = await github.GetCurrentUserAsync();
            settings.Save();
        }

        // Apply auto-start registry setting
        SettingsViewModel.ApplyAutoStart(settings.AutoStartWithWindows);

        // ── Services ───────────────────────────────────────────────
        _polling = new PollingService(github, settings);

        _notifications = new NotificationService();
        _notifications.Initialize();
        _notifications.Subscribe(_polling);

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
        _trayIcon.OnExit(() => Shutdown());

        // ── Start polling ──────────────────────────────────────────
        _polling.Start();
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
}

