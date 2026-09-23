using System.Runtime.InteropServices;
using System.Diagnostics;
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
    private readonly Action? _onResetStatistics;

    /// <summary>Exposes the settings view model so a live window can be kept in sync from outside (e.g. App.xaml.cs).</summary>
    public SettingsViewModel ViewModel => _viewModel;

    public SettingsWindow(SettingsViewModel viewModel, Action? onSaved = null, Action? onResetStatistics = null)
    {
        _viewModel = viewModel;
        _onSaved = onSaved;
        _onResetStatistics = onResetStatistics;
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

    private void ManageFlakinessRules_Click(object sender, RoutedEventArgs e)
    {
        var rulesWindow = new FlakinessRulesWindow(_viewModel) { Owner = this };
        rulesWindow.Show();
    }

    private void LabelRuleAdd_Click(object sender, RoutedEventArgs e) => _viewModel.AddLabelRule();

    private void LabelRuleRemove_Click(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.Button { Tag: SettingsViewModel.LabelRuleViewModel rule })
            _viewModel.RemoveLabelRule(rule);
    }

    /// <summary>Palette swatch or "Default" row: Tag holds the hex value, empty for the CI colour.</summary>
    private void LabelColorPick_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string hex } element
            || FindLabelRule(element) is not { } rule)
            return;

        rule.Color = hex;
        rule.IsColorPickerOpen = false;
    }

    private void LabelColorCustom_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement element || FindLabelRule(element) is not { } rule)
            return;

        rule.IsColorPickerOpen = false;
        using var dialog = new System.Windows.Forms.ColorDialog { FullOpen = true, AnyColor = true };
        if (!rule.IsDefaultColor)
        {
            var current = (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(rule.Color.Trim());
            dialog.Color = System.Drawing.Color.FromArgb(current.R, current.G, current.B);
        }

        var owner = new System.Windows.Forms.NativeWindow();
        owner.AssignHandle(new System.Windows.Interop.WindowInteropHelper(this).Handle);
        try
        {
            if (dialog.ShowDialog(owner) == System.Windows.Forms.DialogResult.OK)
                rule.Color = $"#{dialog.Color.R:X2}{dialog.Color.G:X2}{dialog.Color.B:X2}";
        }
        finally
        {
            owner.ReleaseHandle();
        }
    }

    /// <summary>
    /// The rule a popup element belongs to. Swatches inside the palette have a colour string as
    /// DataContext, so walk up the logical tree to the element bound to the rule.
    /// </summary>
    private static SettingsViewModel.LabelRuleViewModel? FindLabelRule(DependencyObject? element)
    {
        while (element is not null)
        {
            if (element is FrameworkElement { DataContext: SettingsViewModel.LabelRuleViewModel rule })
                return rule;
            element = LogicalTreeHelper.GetParent(element)
                      ?? System.Windows.Media.VisualTreeHelper.GetParent(element);
        }
        return null;
    }

    private void HiddenPrRemove_Click(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.Button { Tag: string key } && !string.IsNullOrWhiteSpace(key))
            _viewModel.RemoveHiddenPr(key);
    }

    private void HiddenPrOpen_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button { Tag: string url } || string.IsNullOrWhiteSpace(url))
            return;

        if (Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps)
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
    }

    private void HiddenReviewerRemove_Click(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.Button { Tag: string login } && !string.IsNullOrWhiteSpace(login))
            _viewModel.RemoveHiddenReviewer(login);
    }

    private void ResetStatistics_Click(object sender, RoutedEventArgs e)
    {
        var result = DarkMessageBox.Show(
            "This will permanently delete all collected statistics. Continue?",
            "Reset statistics",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);
        if (result == MessageBoxResult.Yes)
            _onResetStatistics?.Invoke();
    }
}
