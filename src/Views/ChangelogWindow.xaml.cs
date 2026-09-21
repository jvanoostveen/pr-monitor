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

    private const string DefaultSubtitle = "Relevant entries from CHANGELOG.md";

    private readonly string? _linkUrl;

    public string WindowTitle { get; }

    public string WindowSubtitle { get; }

    public ChangelogWindow(string title, string contentText, string? linkUrl, string? subtitle = null)
    {
        WindowTitle = title;
        WindowSubtitle = string.IsNullOrWhiteSpace(subtitle) ? DefaultSubtitle : subtitle;
        _linkUrl = linkUrl;

        DataContext = this;
        InitializeComponent();

        ContentViewer.Document = MarkdownRenderer.ToFlowDocument(contentText);
    }

    public static void ShowForOwner(
        Window? owner,
        UpdateChangelogResult changelog,
        string? releasePageUrl,
        string? titleOverride = null,
        string? subtitle = null)
    {
        var linkUrl = !string.IsNullOrWhiteSpace(changelog.Url) ? changelog.Url : releasePageUrl;
        var title = string.IsNullOrWhiteSpace(titleOverride) ? changelog.Title : titleOverride;
        var window = new ChangelogWindow(title, changelog.Markdown, linkUrl, subtitle)
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
        if (!string.IsNullOrWhiteSpace(_linkUrl)
            && Uri.TryCreate(_linkUrl, UriKind.Absolute, out var uri)
            && uri.Scheme == Uri.UriSchemeHttps)
        {
            Process.Start(new ProcessStartInfo(_linkUrl) { UseShellExecute = true });
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }
}