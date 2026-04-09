using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace PrMonitor.Views;

public partial class DarkMessageBox : Window
{
    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

    private const int DwmwaUseImmersiveDarkMode = 20;

    public MessageBoxResult Result { get; private set; } = MessageBoxResult.Cancel;

    public DarkMessageBox(string message, string title, MessageBoxButton button, MessageBoxImage icon)
    {
        InitializeComponent();

        Title = title;
        MessageLabel.Text = message;

        ConfigureIcon(icon);
        ConfigureButtons(button);

        // Handle keyboard: Enter = default, Escape = cancel
        KeyDown += (_, e) =>
        {
            if (e.Key == System.Windows.Input.Key.Enter)
            {
                if (OkButton.Visibility == Visibility.Visible)
                    SetResultAndClose(MessageBoxResult.OK);
                else if (YesButton.Visibility == Visibility.Visible)
                    SetResultAndClose(MessageBoxResult.Yes);
            }
            else if (e.Key == System.Windows.Input.Key.Escape)
            {
                if (NoButton.Visibility == Visibility.Visible)
                    SetResultAndClose(MessageBoxResult.No);
                else
                    SetResultAndClose(MessageBoxResult.Cancel);
            }
        };
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var hwnd = new WindowInteropHelper(this).Handle;
        int value = 1;
        DwmSetWindowAttribute(hwnd, DwmwaUseImmersiveDarkMode, ref value, Marshal.SizeOf(value));
    }

    private void ConfigureIcon(MessageBoxImage icon)
    {
        switch (icon)
        {
            case MessageBoxImage.Warning:
                IconLabel.Text = "⚠";
                IconLabel.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xD2, 0x99, 0x22));
                break;
            case MessageBoxImage.Error:
                IconLabel.Text = "✖";
                IconLabel.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xF8, 0x51, 0x49));
                break;
            case MessageBoxImage.Information:
            case MessageBoxImage.Question:
                IconLabel.Text = "ℹ";
                IconLabel.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x58, 0xA6, 0xFF));
                break;
            default:
                IconLabel.Visibility = Visibility.Collapsed;
                break;
        }
    }

    private void ConfigureButtons(MessageBoxButton button)
    {
        switch (button)
        {
            case MessageBoxButton.OK:
                OkButton.Visibility = Visibility.Visible;
                break;
            case MessageBoxButton.YesNo:
                YesButton.Visibility = Visibility.Visible;
                NoButton.Visibility = Visibility.Visible;
                break;
            default:
                OkButton.Visibility = Visibility.Visible;
                break;
        }
    }

    private void SetResultAndClose(MessageBoxResult result)
    {
        Result = result;
        DialogResult = result is MessageBoxResult.OK or MessageBoxResult.Yes;
        Close();
    }

    private void OkButton_Click(object sender, RoutedEventArgs e) => SetResultAndClose(MessageBoxResult.OK);
    private void YesButton_Click(object sender, RoutedEventArgs e) => SetResultAndClose(MessageBoxResult.Yes);
    private void NoButton_Click(object sender, RoutedEventArgs e) => SetResultAndClose(MessageBoxResult.No);

    /// <summary>
    /// Shows a dark-themed message box. Pass <paramref name="owner"/> for proper centering; omit for screen-centered.
    /// </summary>
    public static MessageBoxResult Show(
        string message,
        string title,
        MessageBoxButton button = MessageBoxButton.OK,
        MessageBoxImage icon = MessageBoxImage.None,
        Window? owner = null)
    {
        var dlg = new DarkMessageBox(message, title, button, icon);

        if (owner != null)
        {
            dlg.Owner = owner;
        }
        else
        {
            dlg.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }

        dlg.ShowDialog();
        return dlg.Result;
    }
}
