using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using PrMonitor.ViewModels;

namespace PrMonitor.Views;

public partial class FlakinessRulesWindow : Window
{
    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

    private const int DwmwaUseImmersiveDarkMode = 20;

    private readonly SettingsViewModel _viewModel;

    public FlakinessRulesWindow(SettingsViewModel viewModel)
    {
        _viewModel = viewModel;
        DataContext = viewModel;
        InitializeComponent();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var hwnd = new WindowInteropHelper(this).Handle;
        int value = 1;
        DwmSetWindowAttribute(hwnd, DwmwaUseImmersiveDarkMode, ref value, Marshal.SizeOf(value));
    }

    private void DeleteRule_Click(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.Button btn && btn.Tag is string id)
            _viewModel.DeleteRule(id);
    }
}
