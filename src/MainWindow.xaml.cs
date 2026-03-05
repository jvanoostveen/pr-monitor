using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using PrMonitor.ViewModels;
using WinForms = System.Windows.Forms;

namespace PrMonitor;

/// <summary>
/// Floating window that shows PR lists. Positions itself above the
/// taskbar (bottom-right) and hides on deactivation.
/// </summary>
public partial class MainWindow : Window
{
    private enum SnapCorner { None, TopLeft, TopRight, BottomLeft, BottomRight }

    public MainViewModel ViewModel { get; }
    private DoubleAnimation? _spinAnimation;
    private bool _userMoved;
    private bool _isProgrammaticMove;
    private bool _isDragging;
    private SnapCorner _snappedCorner  = SnapCorner.None;
    private SnapCorner _pendingSnapCorner = SnapCorner.None;

    private const double SnapThreshold = 80;
    private const double SnapInset     = 12;

    public MainWindow(MainViewModel viewModel)
    {
        ViewModel = viewModel;
        DataContext = viewModel;
        InitializeComponent();

        // Drive refresh icon from IsRefreshing property
        viewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.IsRefreshing))
                UpdateRefreshIcon(viewModel.IsRefreshing);
        };

        Loaded += (_, _) =>
        {
            AlignToBottomRight();
        };
    }

    /// <summary>
    /// Show the window. Snaps to bottom-right unless the user has manually moved it.
    /// If the window's monitor is gone, recover it to the same corner on the primary.
    /// </summary>
    public void ShowAtTray()
    {
        if (!_userMoved)
        {
            AlignToBottomRight();
        }
        else
        {
            // If the remembered position is off all screens, recover gracefully.
            EnsureOnScreen();
        }
        Show();
        Activate();
    }

    private void SetWindowPosition(double left, double top)
    {
        _isProgrammaticMove = true;
        Left = left;
        Top = top;
        // Defer reset so any WPF-internal OnLocationChanged calls fired
        // asynchronously by SizeToContent are still suppressed.
        Dispatcher.BeginInvoke(() => _isProgrammaticMove = false);
    }

    private void AlignToBottomRight(double? width = null, double? height = null)
    {
        // Always use primary screen for the default placement
        var primary = WinForms.Screen.PrimaryScreen!.WorkingArea;
        var wa = ScreenRectToWpf(primary);
        double w = width  ?? (ActualWidth  > 0 ? ActualWidth  : Width);
        double h = height ?? (ActualHeight > 0 ? ActualHeight : 0);
        SetWindowPosition(wa.Right - w - SnapInset, wa.Bottom - h - SnapInset);
    }

    // ── Multi-monitor helpers ────────────────────────────────────────────────

    /// <summary>
    /// Convert a physical-pixel screen rect to WPF device-independent units.
    /// </summary>
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

    /// <summary>
    /// Work area of whichever monitor the window is currently displayed on.
    /// Falls back to the primary screen when the handle isn't ready yet.
    /// </summary>
    private Rect GetCurrentWorkArea()
    {
        var handle = new WindowInteropHelper(this).Handle;
        var screen = handle != IntPtr.Zero
            ? WinForms.Screen.FromHandle(handle)
            : WinForms.Screen.PrimaryScreen!;
        return ScreenRectToWpf(screen!.WorkingArea);
    }

    // ── Corner snap helpers ──────────────────────────────────────────────────

    private SnapCorner DetectNearCorner()
    {
        var wa = GetCurrentWorkArea();
        bool nearLeft   = Left < wa.Left + SnapThreshold;
        bool nearRight  = Left + ActualWidth > wa.Right - SnapThreshold;
        bool nearTop    = Top  < wa.Top  + SnapThreshold;
        bool nearBottom = Top  + ActualHeight > wa.Bottom - SnapThreshold;

        if (nearLeft  && nearTop)    return SnapCorner.TopLeft;
        if (nearRight && nearTop)    return SnapCorner.TopRight;
        if (nearLeft  && nearBottom) return SnapCorner.BottomLeft;
        if (nearRight && nearBottom) return SnapCorner.BottomRight;
        return SnapCorner.None;
    }

    private (double left, double top) GetCornerPositionInArea(Rect wa, SnapCorner corner, double? width = null, double? height = null)
    {
        double w = width  ?? ActualWidth;
        double h = height ?? ActualHeight;
        return corner switch
        {
            SnapCorner.TopLeft     => (wa.Left + SnapInset,           wa.Top + SnapInset),
            SnapCorner.TopRight    => (wa.Right  - w - SnapInset,     wa.Top + SnapInset),
            SnapCorner.BottomLeft  => (wa.Left + SnapInset,           wa.Bottom - h - SnapInset),
            SnapCorner.BottomRight => (wa.Right  - w - SnapInset,     wa.Bottom - h - SnapInset),
            _                      => (Left, Top)
        };
    }

    private void ApplyCornerSnap(SnapCorner corner, double? width = null, double? height = null)
    {
        if (corner == SnapCorner.None) return;
        var wa = GetCurrentWorkArea();
        var (l, t) = GetCornerPositionInArea(wa, corner, width, height);
        SetWindowPosition(l, t);
    }

    private static readonly SolidColorBrush SnapActiveBrush =
        new(System.Windows.Media.Color.FromArgb(0xFF, 0x58, 0xA6, 0xFF));
    private static readonly SolidColorBrush NormalBorderBrush =
        new(System.Windows.Media.Color.FromArgb(0xFF, 0x30, 0x36, 0x3D));

    private void UpdateSnapIndicator(SnapCorner potential)
    {
        if (potential != SnapCorner.None)
        {
            OuterBorder.BorderBrush     = SnapActiveBrush;
            OuterBorder.BorderThickness = new Thickness(2);
        }
        else
        {
            OuterBorder.BorderBrush     = NormalBorderBrush;
            OuterBorder.BorderThickness = new Thickness(1);
        }
    }

    /// <summary>
    /// Keep the window fully inside its current monitor's work area.
    /// </summary>
    private void EnsureFullyVisible()
    {
        var wa = GetCurrentWorkArea();
        double left = Left, top = Top;
        if (left + ActualWidth  > wa.Right)  left = wa.Right  - ActualWidth;
        if (top  + ActualHeight > wa.Bottom) top  = wa.Bottom - ActualHeight;
        if (left < wa.Left) left = wa.Left;
        if (top  < wa.Top)  top  = wa.Top;
        if (Math.Abs(left - Left) > 0.5 || Math.Abs(top - Top) > 0.5)
            SetWindowPosition(left, top);
    }

    /// <summary>
    /// Called when showing the window: if it's not visible on any screen
    /// (e.g. the monitor it lived on was disconnected), recover it to the
    /// same logical corner on the primary monitor, clamped to be fully visible.
    /// </summary>
    private void EnsureOnScreen()
    {
        // Check whether the window center lies inside any screen's working area.
        double cx = Left + ActualWidth  / 2;
        double cy = Top  + ActualHeight / 2;

        bool onScreen = false;
        foreach (var screen in WinForms.Screen.AllScreens)
        {
            var wa = ScreenRectToWpf(screen.WorkingArea);
            if (wa.Contains(cx, cy)) { onScreen = true; break; }
        }

        if (!onScreen)
        {
            // Fall back: use the same snap corner (or BottomRight) on the primary.
            var fallbackCorner = _snappedCorner != SnapCorner.None
                ? _snappedCorner
                : SnapCorner.BottomRight;

            var primary = WinForms.Screen.PrimaryScreen!.WorkingArea;
            var wa = ScreenRectToWpf(primary);

            var (l, t) = GetCornerPositionInArea(wa, fallbackCorner);

            // Clamp so the window never overflows the primary work area.
            l = Math.Max(wa.Left, Math.Min(l, wa.Right  - ActualWidth));
            t = Math.Max(wa.Top,  Math.Min(t, wa.Bottom - ActualHeight));

            SetWindowPosition(l, t);
            _snappedCorner = fallbackCorner;
        }
    }

    protected override void OnLocationChanged(EventArgs e)
    {
        base.OnLocationChanged(e);
        if (!_isProgrammaticMove)
        {
            _userMoved = true;
            if (_isDragging)
            {
                _pendingSnapCorner = DetectNearCorner();
                UpdateSnapIndicator(_pendingSnapCorner);
            }
        }
    }

    private void Window_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        // Defer until after WPF finishes all internal layout/position adjustments
        // triggered by SizeToContent, so _isProgrammaticMove is still active.
        var newSize = e.NewSize;
        Dispatcher.BeginInvoke(() =>
        {
            if (_userMoved && _snappedCorner != SnapCorner.None)
                ApplyCornerSnap(_snappedCorner, newSize.Width, newSize.Height);
            else if (_userMoved)
                EnsureFullyVisible();
            else
                AlignToBottomRight(newSize.Width, newSize.Height);
        });
    }

    private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _isDragging       = true;
        _pendingSnapCorner = SnapCorner.None;

        DragMove(); // blocks until mouse is released

        _isDragging = false;
        UpdateSnapIndicator(SnapCorner.None); // clear blue border

        if (_pendingSnapCorner != SnapCorner.None)
        {
            _snappedCorner = _pendingSnapCorner;
            ApplyCornerSnap(_snappedCorner);
        }
        else
        {
            _snappedCorner = SnapCorner.None;
        }
    }

    private void UpdateRefreshIcon(bool isRefreshing)
    {
        var blue = new SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#58A6FF"));
        var grey = new SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#6E7681"));

        if (isRefreshing)
        {
            RefreshIcon.Foreground = blue;
            _spinAnimation = new DoubleAnimation(0, 360, new Duration(TimeSpan.FromMilliseconds(800)))
            {
                RepeatBehavior = RepeatBehavior.Forever,
            };
            RefreshRotate.BeginAnimation(System.Windows.Media.RotateTransform.AngleProperty, _spinAnimation);
        }
        else
        {
            RefreshRotate.BeginAnimation(System.Windows.Media.RotateTransform.AngleProperty, null);
            RefreshRotate.Angle = 0;
            RefreshIcon.Foreground = grey;
        }
    }

    private void CloseButton_Click(object sender, MouseButtonEventArgs e)
    {
        Hide();
    }

    private void RefreshButton_Click(object sender, MouseButtonEventArgs e)
    {
        _ = ViewModel.RefreshAsync();
    }

    private void PrItem_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { Tag: PrItemViewModel vm })
        {
            vm.OpenInBrowser();
        }
    }

    private void AutoMergeHeader_Click(object sender, MouseButtonEventArgs e)
    {
        ViewModel.ToggleAutoMergeExpanded();
        e.Handled = true;
    }

    private void ReviewHeader_Click(object sender, MouseButtonEventArgs e)
    {
        ViewModel.ToggleReviewExpanded();
        e.Handled = true;
    }

    private void HotfixHeader_Click(object sender, MouseButtonEventArgs e)
    {
        ViewModel.ToggleHotfixExpanded();
        e.Handled = true;
    }

    private void MyPrsHeader_Click(object sender, MouseButtonEventArgs e)
    {
        ViewModel.ToggleMyPrsExpanded();
        e.Handled = true;
    }

    private void LaterHeader_Click(object sender, MouseButtonEventArgs e)
    {
        ViewModel.ToggleLaterExpanded();
        e.Handled = true;
    }

    private void PrRow_Hide_Click(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.MenuItem { Tag: PrItemViewModel vm })
            ViewModel.HideItem(vm.Key);
    }

    private void PrRow_Restore_Click(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.MenuItem { Tag: PrItemViewModel vm })
            ViewModel.RestoreItem(vm.Key);
    }

    private void PrRow_CopyBranch_Click(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.MenuItem { Tag: PrItemViewModel vm })
            System.Windows.Clipboard.SetText(vm.HeadRefName);
    }
}