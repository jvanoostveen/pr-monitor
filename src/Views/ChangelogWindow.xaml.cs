using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using PrMonitor.Services;

namespace PrMonitor.Views;

public partial class ChangelogWindow : Window
{
    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

    private const int DwmwaUseImmersiveDarkMode = 20;

    private readonly string? _releasePageUrl;

    public string WindowTitle { get; }
    public string ContentText { get; }

    public ChangelogWindow(string title, string contentText, string? releasePageUrl)
    {
        WindowTitle = title;
        ContentText = contentText;
        _releasePageUrl = releasePageUrl;

        DataContext = this;
        InitializeComponent();
    }

    public static void ShowForOwner(Window? owner, UpdateChangelogResult changelog, string? releasePageUrl)
    {
        var window = new ChangelogWindow(changelog.Title, changelog.Markdown, releasePageUrl)
        {
            Owner = owner is { IsLoaded: true, IsVisible: true } ? owner : null,
            WindowStartupLocation = owner is { IsLoaded: true, IsVisible: true }
                ? WindowStartupLocation.CenterOwner
                : WindowStartupLocation.CenterScreen,
        };

        window.ShowDialog();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var hwnd = new WindowInteropHelper(this).Handle;
        int value = 1;
        DwmSetWindowAttribute(hwnd, DwmwaUseImmersiveDarkMode, ref value, Marshal.SizeOf(value));
    }

    private void OpenReleasePage_Click(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrWhiteSpace(_releasePageUrl)
            && Uri.TryCreate(_releasePageUrl, UriKind.Absolute, out var uri)
            && uri.Scheme == Uri.UriSchemeHttps)
        {
            Process.Start(new ProcessStartInfo(_releasePageUrl) { UseShellExecute = true });
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }
}