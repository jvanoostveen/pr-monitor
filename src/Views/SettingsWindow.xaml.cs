using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using PrMonitor.ViewModels;

namespace PrMonitor.Views;

public partial class SettingsWindow : Window
{
    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

    private const int DwmwaUseImmersiveDarkMode = 20;

    private readonly SettingsViewModel _viewModel;
    private readonly Action? _onSaved;

    public SettingsWindow(SettingsViewModel viewModel, Action? onSaved = null)
    {
        _viewModel = viewModel;
        _onSaved = onSaved;
        DataContext = viewModel;
        InitializeComponent();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        // Request dark title bar from DWM (Windows 10 1903+ / Windows 11)
        var hwnd = new WindowInteropHelper(this).Handle;
        int value = 1;
        DwmSetWindowAttribute(hwnd, DwmwaUseImmersiveDarkMode, ref value, Marshal.SizeOf(value));
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        _viewModel.Save();
        _onSaved?.Invoke();
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }
}
