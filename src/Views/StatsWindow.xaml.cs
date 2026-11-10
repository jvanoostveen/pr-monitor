using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using PrMonitor.Settings;
using PrMonitor.ViewModels;
using WinForms = System.Windows.Forms;

namespace PrMonitor.Views;

public partial class StatsWindow : Window
{
    [DllImport("user32.dll")] private static extern IntPtr CreatePopupMenu();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool AppendMenuW(IntPtr hMenu, uint uFlags, UIntPtr uIDNewItem, string? lpNewItem);
    [DllImport("user32.dll")] private static extern uint TrackPopupMenuEx(IntPtr hmenu, uint fuFlags, int x, int y, IntPtr hwnd, IntPtr lptpm);
    [DllImport("user32.dll")] private static extern bool DestroyMenu(IntPtr hMenu);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hWnd);

    private const uint MF_STRING       = 0x0000;
    private const uint TPM_BOTTOMALIGN = 0x0020;
    private const uint TPM_RETURNCMD   = 0x0100;
    private const uint TPM_RIGHTBUTTON = 0x0002;
    private const uint ID_HIDE_REVIEWER = 1;

    private readonly StatsViewModel _viewModel;
    private readonly AppSettings _settings;

    public StatsWindow(StatsViewModel viewModel, AppSettings settings)
    {
        _viewModel = viewModel;
        _settings = settings;
        DataContext = viewModel;
        InitializeComponent();

        if (_settings.StatsWindowWidth is > 0)
            Width = _settings.StatsWindowWidth.Value;
        if (_settings.StatsWindowHeight is > 0)
            Height = _settings.StatsWindowHeight.Value;

        // When a saved position exists, restore it manually (clamped) instead of centering.
        if (_settings.StatsWindowLeft is not null && _settings.StatsWindowTop is not null)
            WindowStartupLocation = WindowStartupLocation.Manual;
    }

    /// <summary>Rebuild the table from the latest store contents (call when reopening/refreshing).</summary>
    public void RefreshData() => _viewModel.Refresh();

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        DarkTitleBar.Apply(this);

        RestoreSavedPosition();
    }

    private void RestoreSavedPosition()
    {
        if (_settings.StatsWindowLeft is not { } left || _settings.StatsWindowTop is not { } top)
            return;

        var (clampedLeft, clampedTop) = ScreenPlacement.ClampToBestWorkArea(this, left, top, Width, Height);
        Left = clampedLeft;
        Top = clampedTop;
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void AuthorRow_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: AuthorBreakdownRow row })
        {
            e.Handled = true;
            ShowNativeReviewerContextMenu(row);
        }
    }

    private void ShowNativeReviewerContextMenu(AuthorBreakdownRow row)
    {
        var hMenu = CreatePopupMenu();
        if (hMenu == IntPtr.Zero) return;
        try
        {
            AppendMenuW(hMenu, MF_STRING, (UIntPtr)ID_HIDE_REVIEWER, $"Hide {row.DisplayName}");

            var cursor = WinForms.Cursor.Position;
            var hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd != IntPtr.Zero)
                SetForegroundWindow(hwnd);

            var cmd = TrackPopupMenuEx(
                hMenu,
                TPM_BOTTOMALIGN | TPM_RETURNCMD | TPM_RIGHTBUTTON,
                cursor.X,
                cursor.Y,
                hwnd,
                IntPtr.Zero);

            if (cmd == ID_HIDE_REVIEWER)
                _viewModel.HideReviewer(row.Author);
        }
        finally
        {
            DestroyMenu(hMenu);
        }
    }

    private void ToggleShowAllReviewers_Click(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.Button { DataContext: StatRowViewModel row })
            row.ShowAll = !row.ShowAll;
    }

    protected override void OnStateChanged(EventArgs e)
    {
        base.OnStateChanged(e);
        if (WindowState == WindowState.Maximized)
        {
            // Instead of going full-screen, fit the window to show all rows without scrolling.
            WindowState = WindowState.Normal;
            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded,
                new Action(FitToContent));
        }
    }

    /// <summary>
    /// Resize the window to its natural content height (all rows visible, no scrollbar needed).
    /// Caps to 90 % of the current work area to stay on screen.
    /// </summary>
    private void FitToContent()
    {
        var previousSizeToContent = SizeToContent;
        SizeToContent = SizeToContent.Height;
        UpdateLayout();
        var fitHeight = ActualHeight + 1;
        SizeToContent = previousSizeToContent;

        // Cap to 90 % of the monitor work area.
        var wa = ScreenPlacement.CurrentWorkArea(this);
        if (fitHeight > wa.Height * 0.9)
            fitHeight = wa.Height * 0.9;

        Height = fitHeight;

        // Clamp position in case the taller window now goes off-screen.
        var (clampedLeft, clampedTop) = ScreenPlacement.ClampToBestWorkArea(this, Left, Top, Width, Height);
        Left = clampedLeft;
        Top = clampedTop;
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        // Persist size and final on-screen position.
        var bounds = WindowState == WindowState.Normal
            ? new Rect(Left, Top, Width, Height)
            : RestoreBounds;

        _settings.StatsWindowLeft = bounds.Left;
        _settings.StatsWindowTop = bounds.Top;
        _settings.StatsWindowWidth = bounds.Width;
        _settings.StatsWindowHeight = bounds.Height;
        try
        {
            _settings.Save();
        }
        catch
        {
            // Best-effort persistence; never block window close.
        }

        base.OnClosing(e);
    }
}
