using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Navigation;
using PrMonitor.Services;

namespace PrMonitor.Views;

public partial class AboutWindow : Window
{
    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

    private const int DwmwaUseImmersiveDarkMode = 20;

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
        var hwnd = new WindowInteropHelper(this).Handle;
        int value = 1;
        DwmSetWindowAttribute(hwnd, DwmwaUseImmersiveDarkMode, ref value, Marshal.SizeOf(value));
    }

    private void RepoLink_RequestNavigate(object sender, RequestNavigateEventArgs e)
    {
        if (e.Uri.Scheme == Uri.UriSchemeHttps)
            Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
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
