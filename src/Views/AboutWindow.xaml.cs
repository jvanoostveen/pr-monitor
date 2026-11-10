using System.Diagnostics;
using System.Windows;
using System.Windows.Navigation;
using PrMonitor.Services;

namespace PrMonitor.Views;

public partial class AboutWindow : Window
{
    private readonly Action? _checkForUpdatesAction;

    public string VersionText { get; }

    public AboutWindow(string versionText, Action? checkForUpdatesAction = null)
    {
        VersionText = $"Version {versionText}";
        _checkForUpdatesAction = checkForUpdatesAction;

        DataContext = this;
        InitializeComponent();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        DarkTitleBar.Apply(this);
    }

    private void RepoLink_RequestNavigate(object sender, RequestNavigateEventArgs e)
    {
        Browser.OpenHttps(e.Uri.AbsoluteUri);
        e.Handled = true;
    }

    private void CheckForUpdates_Click(object sender, RoutedEventArgs e)
    {
        _checkForUpdatesAction?.Invoke();
    }

    /// <summary>Puts the current memory/handle counters on the clipboard so users can report them.</summary>
    private void CopyDiagnostics_Click(object sender, RoutedEventArgs e)
    {
        var report = $"PR Monitor {VersionText}{Environment.NewLine}" +
                     $"Uptime: {DateTime.Now - Process.GetCurrentProcess().StartTime:d\\.hh\\:mm\\:ss}{Environment.NewLine}" +
                     MemoryDiagnostics.Capture() + Environment.NewLine +
                     MemoryDiagnostics.CaptureNativeBreakdown();

        try
        {
            System.Windows.Clipboard.SetText(report);
            DarkMessageBox.Show("Diagnostics copied to the clipboard.", "Diagnostics", owner: this);
        }
        catch (Exception ex)
        {
            DarkMessageBox.Show($"Could not copy diagnostics: {ex.Message}", "Diagnostics", owner: this);
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }
}
