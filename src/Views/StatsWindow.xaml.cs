using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using PrMonitor.ViewModels;

namespace PrMonitor.Views;

public partial class StatsWindow : Window
{
    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

    private const int DwmwaUseImmersiveDarkMode = 20;

    private readonly StatsViewModel _viewModel;

    public StatsWindow(StatsViewModel viewModel)
    {
        _viewModel = viewModel;
        DataContext = viewModel;
        InitializeComponent();
    }

    /// <summary>Rebuild the table from the latest store contents (call when reopening/refreshing).</summary>
    public void RefreshData() => _viewModel.Refresh();

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var hwnd = new WindowInteropHelper(this).Handle;
        int value = 1;
        DwmSetWindowAttribute(hwnd, DwmwaUseImmersiveDarkMode, ref value, Marshal.SizeOf(value));
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
