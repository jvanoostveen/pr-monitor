using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using PrMonitor.Settings;
using PrMonitor.ViewModels;
using WinForms = System.Windows.Forms;

namespace PrMonitor.Views;

public partial class StatsWindow : Window
{
    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

    private const int DwmwaUseImmersiveDarkMode = 20;

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
        var hwnd = new WindowInteropHelper(this).Handle;
        int value = 1;
        DwmSetWindowAttribute(hwnd, DwmwaUseImmersiveDarkMode, ref value, Marshal.SizeOf(value));

        RestoreSavedPosition();
    }

    private void RestoreSavedPosition()
    {
        if (_settings.StatsWindowLeft is not { } left || _settings.StatsWindowTop is not { } top)
            return;

        var (clampedLeft, clampedTop) = ClampToBestWorkArea(left, top, Width, Height);
        Left = clampedLeft;
        Top = clampedTop;
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

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
        var wa = GetCurrentWorkArea();
        if (fitHeight > wa.Height * 0.9)
            fitHeight = wa.Height * 0.9;

        Height = fitHeight;

        // Clamp position in case the taller window now goes off-screen.
        var (clampedLeft, clampedTop) = ClampToBestWorkArea(Left, Top, Width, Height);
        Left = clampedLeft;
        Top = clampedTop;
    }

    private Rect GetCurrentWorkArea()
    {
        var handle = new WindowInteropHelper(this).Handle;
        var screen = handle != IntPtr.Zero
            ? WinForms.Screen.FromHandle(handle)
            : WinForms.Screen.PrimaryScreen!;
        return ScreenRectToWpf(screen!.WorkingArea);
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

    private Rect ScreenRectToWpf(System.Drawing.Rectangle r)
    {
        var source = PresentationSource.FromVisual(this);
        if (source?.CompositionTarget == null)
            return new Rect(r.X, r.Y, r.Width, r.Height);
        var m = source.CompositionTarget.TransformFromDevice;
        var tl = m.Transform(new System.Windows.Point(r.Left, r.Top));
        var br = m.Transform(new System.Windows.Point(r.Right, r.Bottom));
        return new Rect(tl, br);
    }

    private static double Clamp(double value, double min, double max)
    {
        if (max < min)
            return min;
        return Math.Max(min, Math.Min(value, max));
    }

    private (double left, double top) ClampToWorkArea(Rect wa, double left, double top, double width, double height)
    {
        var clampedLeft = width >= wa.Width ? wa.Left : Clamp(left, wa.Left, wa.Right - width);
        var clampedTop = height >= wa.Height ? wa.Top : Clamp(top, wa.Top, wa.Bottom - height);
        return (clampedLeft, clampedTop);
    }

    /// <summary>
    /// Clamp the requested rectangle to the nearest available monitor work area, recovering
    /// the window onto a visible screen when its previous monitor is no longer connected.
    /// </summary>
    private (double left, double top) ClampToBestWorkArea(double left, double top, double width, double height)
    {
        var targetRect = new Rect(left, top, width, height);

        // If already fully visible on at least one work area, keep the exact position.
        foreach (var screen in WinForms.Screen.AllScreens)
        {
            var wa = ScreenRectToWpf(screen.WorkingArea);
            if (wa.Contains(targetRect.TopLeft) && wa.Contains(targetRect.BottomRight))
                return (left, top);
        }

        var bestDistance = double.PositiveInfinity;
        var bestPosition = (left, top);

        foreach (var screen in WinForms.Screen.AllScreens)
        {
            var wa = ScreenRectToWpf(screen.WorkingArea);
            var candidate = ClampToWorkArea(wa, left, top, width, height);
            var dx = candidate.left - left;
            var dy = candidate.top - top;
            var distance = (dx * dx) + (dy * dy);

            if (distance < bestDistance)
            {
                bestDistance = distance;
                bestPosition = candidate;
            }
        }

        if (double.IsPositiveInfinity(bestDistance))
        {
            var primary = ScreenRectToWpf(WinForms.Screen.PrimaryScreen!.WorkingArea);
            return ClampToWorkArea(primary, left, top, width, height);
        }

        return bestPosition;
    }
}
