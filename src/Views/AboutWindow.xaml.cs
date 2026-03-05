using System.Diagnostics;
using System.Windows;
using System.Windows.Navigation;

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

    private void RepoLink_RequestNavigate(object sender, RequestNavigateEventArgs e)
    {
        Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
        e.Handled = true;
    }

    private void CheckForUpdates_Click(object sender, RoutedEventArgs e)
    {
        _checkForUpdatesAction?.Invoke();
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }
}