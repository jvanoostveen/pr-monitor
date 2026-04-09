using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Runtime.InteropServices;
using PrMonitor.Services;
using PrMonitor.Settings;
using PrMonitor.ViewModels;
using PrMonitor.Views;
using WinForms = System.Windows.Forms;

namespace PrMonitor;

/// <summary>
/// Floating window that shows PR lists. Positions itself above the
/// taskbar (bottom-right) and hides on deactivation.
/// </summary>
public partial class MainWindow : Window
{

    public MainViewModel ViewModel { get; }
    private readonly AppSettings _settings;
    private readonly GitHubService _github;
    private readonly NotificationService _notifications;
    private readonly DiagnosticsLogger _logger;
    private DoubleAnimation? _spinAnimation;
    private bool _userMoved;

    private static readonly SolidColorBrush RefreshBlueBrush;
    private static readonly SolidColorBrush RefreshGreyBrush;

    static MainWindow()
    {
        RefreshBlueBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x58, 0xA6, 0xFF));
        RefreshBlueBrush.Freeze();
        RefreshGreyBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x6E, 0x76, 0x81));
        RefreshGreyBrush.Freeze();
    }

    private bool _isProgrammaticMove;
    private bool _isDragging;
    private SnapCorner _snappedCorner  = SnapCorner.None;
    private SnapCorner _pendingSnapCorner = SnapCorner.None;

    private const double SnapThreshold = 80;
    private const double SnapInset     = 6;

    // Track display configuration changes so window-moves caused by Windows
    // (not the user) don't corrupt the saved position or _userMoved state.
    private bool _displayChangePending;
    private double _preChangeLeft;
    private double _preChangeTop;
    private SnapCorner _preChangeSnappedCorner;
    private bool _preChangeUserMoved;
    private bool _startupPlacementRestored;
    private WinForms.Screen? _snapAnchorScreen;
    private string? _lastLocationSuppressionReason;

    [DllImport("user32.dll")] private static extern IntPtr CreatePopupMenu();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool AppendMenuW(IntPtr hMenu, uint uFlags, UIntPtr uIDNewItem, string? lpNewItem);
    [DllImport("user32.dll")] private static extern uint TrackPopupMenuEx(IntPtr hmenu, uint fuFlags, int x, int y, IntPtr hwnd, IntPtr lptpm);
    [DllImport("user32.dll")] private static extern bool DestroyMenu(IntPtr hMenu);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hWnd);

    private const uint MF_STRING       = 0x0000;
    private const uint MF_SEPARATOR    = 0x0800;
    private const uint MF_GRAYED       = 0x0001;
    private const uint MF_CHECKED      = 0x0008;
    private const uint MF_POPUP        = 0x0010;
    private const uint TPM_BOTTOMALIGN = 0x0020;
    private const uint TPM_RETURNCMD   = 0x0100;
    private const uint TPM_RIGHTBUTTON = 0x0002;

    private const uint ID_PR_COPY_URL      = 1001;
    private const uint ID_PR_COPY_BRANCH   = 1002;
    private const uint ID_PR_RERUN_FAILED  = 1003;
    private const uint ID_PR_COPILOT       = 1004;
    private const uint ID_PR_MOVE_RESTORE  = 1005;
    private const uint ID_DRAFT_READY      = 1006;
    private const uint ID_DRAFT_CONVERT    = 1007;
    private const uint ID_SNOOZE_1H          = 1008;
    private const uint ID_SNOOZE_4H          = 1009;
    private const uint ID_SNOOZE_TOMORROW    = 1010;
    private const uint ID_SNOOZE_1W          = 1011;
    private const uint ID_SNOOZE_INDEFINITELY= 1012;
    private const uint ID_PR_ENABLE_AUTOMERGE= 1013;

    // Assign-reviewer submenu — 2000..2009 assigned, 2010..2019 recent, 2020 search
    private const uint ID_PR_ASSIGN_BASE     = 2000;
    private const uint ID_PR_RECENT_BASE     = 2010;
    private const uint ID_PR_REVIEWER_SEARCH = 2020;

    public MainWindow(MainViewModel viewModel, AppSettings settings, GitHubService github, NotificationService notifications, DiagnosticsLogger logger)
    {
        ViewModel = viewModel;
        _settings = settings;
        _github = github;
        _notifications = notifications;
        _logger = logger;
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
            LogPlacement("Loaded:start", includeScreens: true);
            RestoreStartupPlacement();
            Microsoft.Win32.SystemEvents.DisplaySettingsChanging += OnDisplaySettingsChanging;
            Microsoft.Win32.SystemEvents.DisplaySettingsChanged  += OnDisplaySettingsChanged;
        };
    }

    /// <summary>
    /// Re-applies the active corner snap, if any, after WPF layout has settled.
    /// Call this after resource changes that affect window height (e.g. compact mode toggle)
    /// so a snapped window stays flush with the correct corner.
    /// </summary>
    internal void ReapplySnapIfSnapped()
    {
        if (_snappedCorner != SnapCorner.None)
            ApplyCornerSnap(_snappedCorner);
    }

    /// <summary>
    /// Show the window. Snaps to bottom-right unless the user has manually moved it.
    /// If the window's monitor is gone, recover it to the same corner on the primary.
    /// </summary>
    public void ShowAtTray()
    {
        LogPlacement("ShowAtTray:start");
        Show();

        // On first show, Loaded restores placement from settings.
        // Do not run fallback alignment before this, or it can overwrite in-memory settings.
        if (!_startupPlacementRestored)
        {
            LogPlacement("ShowAtTray:skipped", "startup-not-restored");
            return;
        }

        if (_userMoved)
        {
            // If the remembered position is off all screens, recover gracefully.
            EnsureOnScreen();
        }
        else
        {
            AlignToBottomRight();
        }

        Activate();
        Focus();
        PersistWindowState(isVisible: true);
        LogPlacement("ShowAtTray:end");
    }

    /// <summary>
    /// Hide the window and persist hidden state.
    /// </summary>
    public void HideToTray()
    {
        LogPlacement("HideToTray:start");
        PersistWindowPosition();
        Hide();
        PersistWindowState(isVisible: false);
    }

    /// <summary>
    /// Persist current window state (used during app shutdown).
    /// </summary>
    public void PersistCurrentWindowState()
    {
        PersistWindowPosition();
        PersistWindowState(IsVisible);
    }

    protected override void OnClosed(EventArgs e)
    {
        Microsoft.Win32.SystemEvents.DisplaySettingsChanging -= OnDisplaySettingsChanging;
        Microsoft.Win32.SystemEvents.DisplaySettingsChanged  -= OnDisplaySettingsChanged;
        base.OnClosed(e);
    }

    private void SetWindowPosition(double left, double top, string reason = "unspecified")
    {
        var fromLeft = Left;
        var fromTop = Top;
        _isProgrammaticMove = true;
        Left = left;
        Top = top;
        // Defer reset so any WPF-internal OnLocationChanged calls fired
        // asynchronously by SizeToContent are still suppressed.
        Dispatcher.BeginInvoke(() => _isProgrammaticMove = false);
    }

    private void AlignToBottomRight(double? width = null, double? height = null, string reason = "align-bottom-right")
    {
        // Always use primary screen for the default placement
        var primary = WinForms.Screen.PrimaryScreen!.WorkingArea;
        var wa = ScreenRectToWpf(primary);
        double w = width  ?? (ActualWidth  > 0 ? ActualWidth  : Width);
        double h = height ?? (ActualHeight > 0 ? ActualHeight : 0);
        var left = wa.Right - w - SnapInset;
        var top = wa.Bottom - h - SnapInset;
        SetWindowPosition(left, top, reason);
        _snapAnchorScreen = WinForms.Screen.PrimaryScreen;
    }

    private void RestoreStartupPlacement()
    {
        LogPlacement("RestoreStartupPlacement:start", $"saved=({FormatNullable(_settings.MainWindowLeft)},{FormatNullable(_settings.MainWindowTop)}); savedCorner={_settings.MainWindowSnappedCorner}; visibleSetting={_settings.MainWindowVisible}", includeScreens: true);
        if (_settings.MainWindowLeft is not double savedLeft || _settings.MainWindowTop is not double savedTop)
        {
            AlignToBottomRight(reason: "startup-no-saved-position");
            _startupPlacementRestored = true;
            LogPlacement("RestoreStartupPlacement:end", "branch=no-saved-position");
            return;
        }

        var width = ActualWidth > 0 ? ActualWidth : Width;
        var height = ActualHeight > 0 ? ActualHeight : Height;
        var (left, top) = ClampToBestWorkArea(savedLeft, savedTop, width, height);
        SetWindowPosition(left, top, "startup-restore-clamped");
        _snapAnchorScreen = FindBestScreenForRect(left, top, width, height);
        _userMoved = true;
        if (_settings.MainWindowSnappedCorner is { } cornerStr
            && Enum.TryParse<SnapCorner>(cornerStr, out var restoredCorner))
            _snappedCorner = restoredCorner;

        if (_snappedCorner != SnapCorner.None && _snapAnchorScreen is not null)
        {
            var wa = ScreenRectToWpf(_snapAnchorScreen.WorkingArea);
            var (snapLeft, snapTop) = GetCornerPositionInArea(wa, _snappedCorner, width, height);
            SetWindowPosition(snapLeft, snapTop, "startup-restore-snap");
        }

        PersistWindowPosition();
        _startupPlacementRestored = true;
        LogPlacement("RestoreStartupPlacement:end", $"final=({FormatDouble(Left)},{FormatDouble(Top)}); anchor={DescribeScreen(_snapAnchorScreen)}");
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

    private static double Clamp(double value, double min, double max)
    {
        if (max < min)
            return min;
        return Math.Max(min, Math.Min(value, max));
    }

    private static double ComputeOverlapArea(Rect a, Rect b) =>
        SnapHelper.ComputeOverlapArea(a, b);

    private (double left, double top) ClampToWorkArea(Rect wa, double left, double top, double width, double height)
    {
        var clampedLeft = width >= wa.Width ? wa.Left : Clamp(left, wa.Left, wa.Right - width);
        var clampedTop = height >= wa.Height ? wa.Top : Clamp(top, wa.Top, wa.Bottom - height);
        return (clampedLeft, clampedTop);
    }

    private (double left, double top) ClampToBestWorkArea(double left, double top, double width, double height)
    {
        var targetRect = new Rect(left, top, width, height);
        var bestDistance = double.PositiveInfinity;
        var bestOverlap = double.NegativeInfinity;
        var bestPosition = (left, top);

        foreach (var screen in WinForms.Screen.AllScreens)
        {
            var wa = ScreenRectToWpf(screen.WorkingArea);
            var candidate = ClampToWorkArea(wa, left, top, width, height);
            var dx = candidate.left - left;
            var dy = candidate.top - top;
            var distance = (dx * dx) + (dy * dy);

            var candidateRect = new Rect(candidate.left, candidate.top, width, height);
            var overlap = ComputeOverlapArea(candidateRect, wa);

            if (distance < bestDistance - 0.01 || (Math.Abs(distance - bestDistance) <= 0.01 && overlap > bestOverlap))
            {
                bestDistance = distance;
                bestOverlap = overlap;
                bestPosition = candidate;
            }
        }

        if (double.IsPositiveInfinity(bestDistance))
        {
            var primary = ScreenRectToWpf(WinForms.Screen.PrimaryScreen!.WorkingArea);
            return ClampToWorkArea(primary, left, top, width, height);
        }

        // If already fully visible on at least one work area, preserve exact position.
        foreach (var screen in WinForms.Screen.AllScreens)
        {
            var wa = ScreenRectToWpf(screen.WorkingArea);
            if (wa.Contains(targetRect.TopLeft) && wa.Contains(targetRect.BottomRight))
                return (left, top);
        }

        return bestPosition;
    }

    private void PersistWindowPosition(bool saveToDisk = true)
    {
        if (!double.IsNaN(Left) && !double.IsNaN(Top) && !double.IsInfinity(Left) && !double.IsInfinity(Top))
        {
            var previousLeft = _settings.MainWindowLeft;
            var previousTop = _settings.MainWindowTop;
            var previousCorner = _settings.MainWindowSnappedCorner;
            _settings.MainWindowLeft = Left;
            _settings.MainWindowTop = Top;
            _settings.MainWindowSnappedCorner = _snappedCorner == SnapCorner.None ? null : _snappedCorner.ToString();
            if (previousLeft != _settings.MainWindowLeft
                || previousTop != _settings.MainWindowTop
                || previousCorner != _settings.MainWindowSnappedCorner)
            {
                LogPlacement("PersistWindowPosition", $"saveToDisk={saveToDisk}; saved=({FormatNullable(_settings.MainWindowLeft)},{FormatNullable(_settings.MainWindowTop)}); savedCorner={_settings.MainWindowSnappedCorner}");
            }
            if (saveToDisk)
                _settings.Save();
        }
    }

    private void PersistWindowState(bool isVisible)
    {
        var previousVisibility = _settings.MainWindowVisible;
        _settings.MainWindowVisible = isVisible;
        if (previousVisibility != isVisible)
            LogPlacement("PersistWindowState", $"visible={isVisible}");
        _settings.Save();
    }

    // ── Corner snap helpers ──────────────────────────────────────────────────

    private SnapCorner DetectNearCorner()
    {
        var w  = ActualWidth  > 0 ? ActualWidth  : Width;
        var h  = ActualHeight > 0 ? ActualHeight : Height;
        var screen = FindBestScreenForRect(Left, Top, w, h);
        var wa = ScreenRectToWpf(screen.WorkingArea);
        return SnapHelper.DetectNearCorner(Left, Top, w, h, wa, SnapThreshold);
    }

    private (double left, double top) GetCornerPositionInArea(Rect wa, SnapCorner corner, double? width = null, double? height = null)
    {
        double w = width  ?? ActualWidth;
        double h = height ?? ActualHeight;
        return SnapHelper.GetCornerPosition(wa, corner, w, h, SnapInset);
    }

    private void ApplyCornerSnap(SnapCorner corner, double? width = null, double? height = null)
    {
        if (corner == SnapCorner.None)
            return;

        var w = width ?? (ActualWidth > 0 ? ActualWidth : Width);
        var h = height ?? (ActualHeight > 0 ? ActualHeight : Height);
        if (double.IsNaN(w) || double.IsInfinity(w) || w <= 0)
            w = 380;
        if (double.IsNaN(h) || double.IsInfinity(h) || h <= 0)
            h = 240;

        // Prefer an anchored monitor for snapped windows so post-startup layout changes
        // do not drift to a different monitor.
        var screen = _snapAnchorScreen ?? FindBestScreenForRect(Left, Top, w, h);
        var wa = ScreenRectToWpf(screen.WorkingArea);
        var (l, t) = GetCornerPositionInArea(wa, corner, w, h);
        LogPlacement("ApplyCornerSnap", $"corner={corner}; size=({FormatDouble(w)},{FormatDouble(h)}); screen={DescribeScreen(screen)}; target=({FormatDouble(l)},{FormatDouble(t)})");
        SetWindowPosition(l, t, "apply-corner-snap");
        _snapAnchorScreen = screen;
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
        {
            LogPlacement("EnsureFullyVisible", $"current=({FormatDouble(Left)},{FormatDouble(Top)}); corrected=({FormatDouble(left)},{FormatDouble(top)}); workArea={DescribeRect(wa)}");
            SetWindowPosition(left, top, "ensure-fully-visible");
        }
    }

    /// <summary>
    /// Called when showing the window: if it's not visible on any screen
    /// (e.g. the monitor it lived on was disconnected), recover it to the
    /// same logical corner on the primary monitor, clamped to be fully visible.
    /// </summary>
    private void EnsureOnScreen()
    {
        var width = ActualWidth > 0 ? ActualWidth : Width;
        var height = ActualHeight > 0 ? ActualHeight : Height;
        var (left, top) = ClampToBestWorkArea(Left, Top, width, height);
        LogPlacement("EnsureOnScreen", $"current=({FormatDouble(Left)},{FormatDouble(Top)}); clamped=({FormatDouble(left)},{FormatDouble(top)}); size=({FormatDouble(width)},{FormatDouble(height)})");
        if (Math.Abs(left - Left) > 0.5 || Math.Abs(top - Top) > 0.5)
            SetWindowPosition(left, top, "ensure-on-screen");

        if (_snappedCorner != SnapCorner.None)
            _snapAnchorScreen = FindBestScreenForRect(left, top, width, height);

        PersistWindowPosition();
    }

    // ── Display change handling ──────────────────────────────────────────────

    private void OnDisplaySettingsChanging(object? sender, EventArgs e)
    {
        // Fires BEFORE Windows repositions windows — save the intended position now.
        // May fire on a background thread; use Invoke to read WPF properties safely.
        Dispatcher.Invoke(() =>
        {
            _displayChangePending   = true;
            _preChangeLeft          = Left;
            _preChangeTop           = Top;
            _preChangeSnappedCorner = _snappedCorner;
            _preChangeUserMoved     = _userMoved;
            LogPlacement("DisplaySettingsChanging", $"preChange=({FormatDouble(_preChangeLeft)},{FormatDouble(_preChangeTop)}); preCorner={_preChangeSnappedCorner}; preUserMoved={_preChangeUserMoved}", includeScreens: true);
        });
    }

    private void OnDisplaySettingsChanged(object? sender, EventArgs e)
    {
        // Fires AFTER display settings change is complete. May be on background thread.
        Dispatcher.BeginInvoke(() =>
        {
            _displayChangePending = false;
            ReapplyPositionAfterDisplayChange();
            PersistWindowPosition();
            LogPlacement("DisplaySettingsChanged:end", includeScreens: true);
        });
    }

    /// <summary>
    /// After a display configuration change, reposition the window based on WHERE
    /// it was BEFORE the change, not where Windows randomly dumped it.
    /// </summary>
    private void ReapplyPositionAfterDisplayChange()
    {
        var width  = ActualWidth  > 0 ? ActualWidth  : Width;
        var height = ActualHeight > 0 ? ActualHeight : Height;

        if (!_preChangeUserMoved)
        {
            AlignToBottomRight(width, height, "display-change-align-bottom-right");
            return;
        }

        if (_preChangeSnappedCorner != SnapCorner.None)
        {
            // Find the screen that best matches the pre-change position, then re-snap
            // to the same corner on that screen (which may now be a different screen
            // if the original monitor was disconnected).
            var screen = FindBestScreenForRect(_preChangeLeft, _preChangeTop, width, height);
            var wa = ScreenRectToWpf(screen.WorkingArea);
            var (l, t) = GetCornerPositionInArea(wa, _preChangeSnappedCorner, width, height);
            _snappedCorner = _preChangeSnappedCorner;
            _snapAnchorScreen = screen;
            SetWindowPosition(l, t, "display-change-resnap");
        }
        else
        {
            // No snap: clamp the pre-change position to the best available work area.
            var (l, t) = ClampToBestWorkArea(_preChangeLeft, _preChangeTop, width, height);
            _snapAnchorScreen = null;
            SetWindowPosition(l, t, "display-change-clamp");
        }
    }

    /// <summary>
    /// Returns the screen whose work area overlaps most with the given rect.
    /// Falls back to the closest screen (by center distance) when there is no overlap,
    /// then to the primary screen.
    /// </summary>
    private WinForms.Screen FindBestScreenForRect(double left, double top, double width, double height)
    {
        var windowRect   = new Rect(left, top, width, height);
        var windowCenter = new System.Windows.Point(left + width / 2, top + height / 2);

        WinForms.Screen? bestOverlap  = null;
        double maxOverlap = 0;
        WinForms.Screen? closest      = null;
        double minDist    = double.MaxValue;

        foreach (var screen in WinForms.Screen.AllScreens)
        {
            var wa      = ScreenRectToWpf(screen.WorkingArea);
            var overlap = ComputeOverlapArea(windowRect, wa);
            if (overlap > maxOverlap)
            {
                maxOverlap = overlap;
                bestOverlap = screen;
            }
            var cx   = (wa.Left + wa.Right)  / 2;
            var cy   = (wa.Top  + wa.Bottom) / 2;
            var dist = (windowCenter.X - cx) * (windowCenter.X - cx)
                     + (windowCenter.Y - cy) * (windowCenter.Y - cy);
            if (dist < minDist)
            {
                minDist = dist;
                closest = screen;
            }
        }

        return bestOverlap ?? closest ?? WinForms.Screen.PrimaryScreen!;
    }

    protected override void OnLocationChanged(EventArgs e)
    {
        base.OnLocationChanged(e);
        if (_isProgrammaticMove)
        {
            LogLocationSuppression("programmatic");
            return;
        }

        if (_displayChangePending)
        {
            LogLocationSuppression("display-change-pending");
            return;
        }

        _lastLocationSuppressionReason = null;
        if (!_isProgrammaticMove && !_displayChangePending)
        {
            _userMoved = true;
            PersistWindowPosition(saveToDisk: false);
            if (_isDragging)
            {
                _pendingSnapCorner = DetectNearCorner();
                UpdateSnapIndicator(_pendingSnapCorner);
            }
            else if (_snappedCorner == SnapCorner.None)
            {
                _snapAnchorScreen = null;
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
            if (!_startupPlacementRestored)
                return;

            if (_isDragging)
                return;

            if (_userMoved && _snappedCorner != SnapCorner.None)
                ApplyCornerSnap(_snappedCorner, newSize.Width, newSize.Height);
            else if (_userMoved)
                EnsureFullyVisible();
            else
                AlignToBottomRight(newSize.Width, newSize.Height, "size-changed-align-bottom-right");
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
            _snapAnchorScreen = null; // clear so ApplyCornerSnap picks the best screen for current drag position
            ApplyCornerSnap(_snappedCorner);
            // _snapAnchorScreen is now set by ApplyCornerSnap to the correct target screen
            LogPlacement("Header_MouseLeftButtonDown", $"snap-applied={_snappedCorner}; anchor={DescribeScreen(_snapAnchorScreen)}");
        }
        else
        {
            _snappedCorner = SnapCorner.None;
            _snapAnchorScreen = null;
            LogPlacement("Header_MouseLeftButtonDown", "snap-cleared");
        }

        PersistWindowPosition();
    }

    private void LogLocationSuppression(string reason)
    {
        if (_lastLocationSuppressionReason == reason)
            return;

        _lastLocationSuppressionReason = reason;
        LogPlacement("OnLocationChanged:suppressed", $"reason={reason}");
    }

    private void LogPlacement(string phase, string? reason = null, bool includeScreens = false)
    {
        var message = $"MainWindowPlacement phase={phase}";
        if (!string.IsNullOrWhiteSpace(reason))
            message += $" reason={reason}";

        message += $" state={DescribePlacementState()} settings={DescribeSettingsState()}";
        if (includeScreens)
            message += $" screens={DescribeAllScreens()}";

        _logger.Info(message);
    }

    private string DescribePlacementState()
    {
        return $"pos=({FormatDouble(Left)},{FormatDouble(Top)}); actual=({FormatDouble(ActualWidth)},{FormatDouble(ActualHeight)}); size=({FormatDouble(Width)},{FormatDouble(Height)}); visible={IsVisible}; windowState={WindowState}; startupRestored={_startupPlacementRestored}; userMoved={_userMoved}; programmatic={_isProgrammaticMove}; displayChangePending={_displayChangePending}; dragging={_isDragging}; snapped={_snappedCorner}; pendingSnap={_pendingSnapCorner}; anchor={DescribeScreen(_snapAnchorScreen)}";
    }

    private string DescribeSettingsState()
    {
        return $"savedPos=({FormatNullable(_settings.MainWindowLeft)},{FormatNullable(_settings.MainWindowTop)}); savedCorner={_settings.MainWindowSnappedCorner}; savedVisible={_settings.MainWindowVisible}";
    }

    private string DescribeAllScreens()
    {
        return string.Join(" | ", WinForms.Screen.AllScreens.Select(DescribeScreen));
    }

    private string DescribeScreen(WinForms.Screen? screen)
    {
        if (screen is null)
            return "none";

        var wa = ScreenRectToWpf(screen.WorkingArea);
        return $"{screen.DeviceName}[primary={screen.Primary}; workArea={DescribeRect(wa)}]";
    }

    private static string DescribeRect(Rect rect)
    {
        return $"({FormatDouble(rect.Left)},{FormatDouble(rect.Top)},{FormatDouble(rect.Width)},{FormatDouble(rect.Height)})";
    }

    private static string FormatDouble(double value)
    {
        if (double.IsNaN(value))
            return "NaN";
        if (double.IsPositiveInfinity(value))
            return "+Inf";
        if (double.IsNegativeInfinity(value))
            return "-Inf";
        return value.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);
    }

    private static string FormatNullable(double? value)
    {
        return value is double actual ? FormatDouble(actual) : "null";
    }

    private void UpdateRefreshIcon(bool isRefreshing)
    {
        if (isRefreshing)
        {
            RefreshIcon.Foreground = RefreshBlueBrush;
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
            RefreshIcon.Foreground = RefreshGreyBrush;
            _spinAnimation = null;
        }
    }

    private void CloseButton_Click(object sender, MouseButtonEventArgs e)
    {
        HideToTray();
    }

    private void RefreshButton_Click(object sender, MouseButtonEventArgs e)
    {
        _ = ViewModel.RefreshAsync();
    }

    private void Window_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.Escape)
        {
            HideToTray();
            e.Handled = true;
        }
        else if (e.Key == System.Windows.Input.Key.R && e.KeyboardDevice.Modifiers == System.Windows.Input.ModifierKeys.None)
        {
            _ = ViewModel.RefreshAsync();
            e.Handled = true;
        }
    }

    private void PrItem_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { Tag: PrItemViewModel vm })
        {
            vm.OpenInBrowser();
        }
    }

    private void PrRow_PreviewMouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: PrItemViewModel vm })
            return;

        e.Handled = true;
        ShowNativePrContextMenu(vm);
    }

    private void ShowNativePrContextMenu(PrItemViewModel vm)
    {
        var hMenu = CreatePopupMenu();
        if (hMenu == IntPtr.Zero)
            return;

        // Collect assigned/recent logins for submenu ID mapping
        var assignedLogins = vm.ReviewerLogins.Take(10).ToArray();
        var recentLogins = _settings.RecentReviewers
            .Where(r => !assignedLogins.Contains(r, StringComparer.OrdinalIgnoreCase))
            .Take(10)
            .ToArray();

        try
        {
            var isHidden = _settings.HiddenPrKeys.Contains(vm.Key);
            var rerunFlags = vm.CanRerunFailedJobs ? MF_STRING : MF_STRING | MF_GRAYED;
            var copilotFlags = vm.CanRequestCopilotReview ? MF_STRING : MF_STRING | MF_GRAYED;
            var snoozeMenu = IntPtr.Zero;

            AppendMenuW(hMenu, MF_STRING, (UIntPtr)ID_PR_COPY_URL, "Copy PR URL");
            AppendMenuW(hMenu, MF_STRING, (UIntPtr)ID_PR_COPY_BRANCH, "Copy branch name");
            AppendMenuW(hMenu, MF_SEPARATOR, UIntPtr.Zero, null);
            AppendMenuW(hMenu, rerunFlags, (UIntPtr)ID_PR_RERUN_FAILED, "Rerun failed jobs");
            AppendMenuW(hMenu, copilotFlags, (UIntPtr)ID_PR_COPILOT, "Request Copilot review");

            // "Assign reviewer" submenu — only for own non-draft PRs
            if (vm.IsOwnPr && !vm.IsDraft)
            {
                AppendMenuW(hMenu, MF_SEPARATOR, UIntPtr.Zero, null);
                var autoMergeFlags = vm.CanEnableAutoMerge ? MF_STRING : MF_STRING | MF_GRAYED;
                AppendMenuW(hMenu, autoMergeFlags, (UIntPtr)ID_PR_ENABLE_AUTOMERGE, "Enable auto-merge");

                var assignMenu = CreatePopupMenu();
                // Assigned reviewers (checked — click removes)
                for (int i = 0; i < assignedLogins.Length; i++)
                    AppendMenuW(assignMenu, MF_STRING | MF_CHECKED, (UIntPtr)(ID_PR_ASSIGN_BASE + (uint)i), assignedLogins[i]);
                // Recent reviewers not yet assigned
                if (recentLogins.Length > 0)
                {
                    if (assignedLogins.Length > 0)
                        AppendMenuW(assignMenu, MF_SEPARATOR, UIntPtr.Zero, null);
                    for (int i = 0; i < recentLogins.Length; i++)
                        AppendMenuW(assignMenu, MF_STRING, (UIntPtr)(ID_PR_RECENT_BASE + (uint)i), recentLogins[i]);
                }
                // Only add separator before Search… when there are items above it
                if (assignedLogins.Length > 0 || recentLogins.Length > 0)
                    AppendMenuW(assignMenu, MF_SEPARATOR, UIntPtr.Zero, null);
                AppendMenuW(assignMenu, MF_STRING, (UIntPtr)ID_PR_REVIEWER_SEARCH, "Search…");
                AppendMenuW(hMenu, MF_POPUP, (UIntPtr)(ulong)assignMenu.ToInt64(), "Assign reviewer");
            }

            if (isHidden)
            {
                AppendMenuW(hMenu, MF_STRING, (UIntPtr)ID_PR_MOVE_RESTORE, "Restore");
            }
            else
            {
                snoozeMenu = CreatePopupMenu();
                AppendMenuW(snoozeMenu, MF_STRING, (UIntPtr)ID_SNOOZE_1H, "1 hour");
                AppendMenuW(snoozeMenu, MF_STRING, (UIntPtr)ID_SNOOZE_4H, "4 hours");
                AppendMenuW(snoozeMenu, MF_STRING, (UIntPtr)ID_SNOOZE_TOMORROW, "Tomorrow morning (09:00)");
                AppendMenuW(snoozeMenu, MF_STRING, (UIntPtr)ID_SNOOZE_1W, "1 week");
                AppendMenuW(snoozeMenu, MF_SEPARATOR, UIntPtr.Zero, null);
                AppendMenuW(snoozeMenu, MF_STRING, (UIntPtr)ID_SNOOZE_INDEFINITELY, "Indefinitely");
                AppendMenuW(hMenu, MF_POPUP, (UIntPtr)(ulong)snoozeMenu.ToInt64(), "Move to later");
            }
            if (vm.CanMarkAsReady || vm.CanConvertToDraft)
            {
                AppendMenuW(hMenu, MF_SEPARATOR, UIntPtr.Zero, null);
                if (vm.CanMarkAsReady)
                    AppendMenuW(hMenu, MF_STRING, (UIntPtr)ID_DRAFT_READY, "Mark as ready");
                else
                    AppendMenuW(hMenu, MF_STRING, (UIntPtr)ID_DRAFT_CONVERT, "Convert to draft");
            }

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

            switch (cmd)
            {
                case ID_PR_COPY_URL:
                    System.Windows.Clipboard.SetText(vm.Url);
                    break;
                case ID_PR_COPY_BRANCH:
                    System.Windows.Clipboard.SetText(vm.HeadRefName);
                    break;
                case ID_PR_RERUN_FAILED:
                    if (vm.CanRerunFailedJobs)
                        _ = RerunFailedJobsAsync(vm);
                    break;
                case ID_PR_COPILOT:
                    if (vm.CanRequestCopilotReview)
                        _ = RequestCopilotReviewAsync(vm);
                    break;
                case ID_PR_ENABLE_AUTOMERGE:
                    if (vm.CanEnableAutoMerge)
                        _ = EnableAutoMergeAsync(vm);
                    break;
                case ID_PR_REVIEWER_SEARCH:
                    _ = SearchAndAssignReviewerAsync(vm);
                    break;
                case ID_PR_MOVE_RESTORE:
                    ViewModel.RestoreItem(vm.Key);
                    break;
                case ID_SNOOZE_1H:
                    ViewModel.HideItem(vm.Key, DateTimeOffset.UtcNow.AddHours(1));
                    break;
                case ID_SNOOZE_4H:
                    ViewModel.HideItem(vm.Key, DateTimeOffset.UtcNow.AddHours(4));
                    break;
                case ID_SNOOZE_TOMORROW:
                {
                    var tomorrow = DateTimeOffset.Now.Date.AddDays(1).AddHours(9);
                    ViewModel.HideItem(vm.Key, new DateTimeOffset(tomorrow, DateTimeOffset.Now.Offset));
                    break;
                }
                case ID_SNOOZE_1W:
                    ViewModel.HideItem(vm.Key, DateTimeOffset.UtcNow.AddDays(7));
                    break;
                case ID_SNOOZE_INDEFINITELY:
                    ViewModel.HideItem(vm.Key, null);
                    break;
                case ID_DRAFT_READY:
                    if (vm.CanMarkAsReady)
                        _ = SetPrReadyAsync(vm);
                    break;
                case ID_DRAFT_CONVERT:
                    if (vm.CanConvertToDraft)
                        _ = SetPrDraftAsync(vm);
                    break;
                default:
                    // Assigned reviewer range: remove reviewer
                    if (cmd >= ID_PR_ASSIGN_BASE && cmd < ID_PR_ASSIGN_BASE + (uint)assignedLogins.Length)
                    {
                        var login = assignedLogins[cmd - ID_PR_ASSIGN_BASE];
                        _ = RemoveReviewerAsync(vm, login);
                    }
                    // Recent reviewer range: assign reviewer
                    else if (cmd >= ID_PR_RECENT_BASE && cmd < ID_PR_RECENT_BASE + (uint)recentLogins.Length)
                    {
                        var login = recentLogins[cmd - ID_PR_RECENT_BASE];
                        _ = AssignReviewerAsync(vm, login);
                    }
                    break;
            }
        }
        finally
        {
            DestroyMenu(hMenu);
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

    private void DependabotHeader_Click(object sender, MouseButtonEventArgs e)
    {
        ViewModel.ToggleDependabotExpanded();
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

    private void TeamReviewHeader_Click(object sender, MouseButtonEventArgs e)
    {
        ViewModel.ToggleTeamReviewExpanded();
        e.Handled = true;
    }

    private void LaterHeader_Click(object sender, MouseButtonEventArgs e)
    {
        ViewModel.ToggleLaterExpanded();
        e.Handled = true;
    }

    private void DraftPrsHeader_Click(object sender, MouseButtonEventArgs e)
    {
        ViewModel.ToggleDraftExpanded();
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

    private void PrRow_CopyUrl_Click(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.MenuItem { Tag: PrItemViewModel vm })
            System.Windows.Clipboard.SetText(vm.Url);
    }

    private async void PrRow_RerunFailedJobs_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.MenuItem { Tag: PrItemViewModel vm })
            return;

        await RerunFailedJobsAsync(vm);
    }

    private async Task RerunFailedJobsAsync(PrItemViewModel vm)
    {
        if (!vm.CanRerunFailedJobs)
            return;

        if (!TrySplitRepository(vm.Repository, out var owner, out var repo))
        {
            DarkMessageBox.Show("Could not determine owner/repository for this PR.", "Rerun failed jobs",
                MessageBoxButton.OK, MessageBoxImage.Warning, this);
            return;
        }

        if (string.IsNullOrWhiteSpace(vm.HeadCommitSha))
        {
            DarkMessageBox.Show("No head commit SHA found for this PR.", "Rerun failed jobs",
                MessageBoxButton.OK, MessageBoxImage.Warning, this);
            return;
        }

        try
        {
            var failedRunIds = await _github.FetchFailedRunIdsAsync(owner, repo, vm.HeadCommitSha);
            if (failedRunIds.Count == 0)
            {
                DarkMessageBox.Show("No failed workflow runs were found for this PR commit.", "Rerun failed jobs",
                    MessageBoxButton.OK, MessageBoxImage.Information, this);
                return;
            }

            var successfulReruns = 0;
            foreach (var runId in failedRunIds)
            {
                if (await _github.RerunFailedJobsAsync(owner, repo, runId))
                    successfulReruns++;
            }

            if (successfulReruns > 0)
            {
                _notifications.Notify(
                    $"Rerun started for PR #{vm.Number}",
                    $"Triggered rerun for {successfulReruns} failed workflow run(s) in {vm.Repository}.");
            }
            else
            {
                DarkMessageBox.Show(
                    "Failed to trigger rerun for the failed workflow runs.",
                    "Rerun failed jobs",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning,
                    this);
            }
        }
        catch (Exception ex)
        {
            DarkMessageBox.Show(
                $"Could not rerun failed jobs.\n\nDetails: {ex.Message}",
                "Rerun failed jobs",
                MessageBoxButton.OK,
                MessageBoxImage.Error,
                this);
        }
    }

    private async void PrRow_RequestCopilotReview_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.MenuItem { Tag: PrItemViewModel vm })
            return;

        await RequestCopilotReviewAsync(vm);
    }

    private async Task RequestCopilotReviewAsync(PrItemViewModel vm)
    {
        if (!vm.CanRequestCopilotReview)
            return;

        if (!TrySplitRepository(vm.Repository, out var owner, out var repo))
        {
            DarkMessageBox.Show(
                "Could not determine owner/repository for this PR.",
                "Request Copilot review",
                MessageBoxButton.OK,
                MessageBoxImage.Warning,
                this);
            return;
        }

        try
        {
            var success = await _github.RequestCopilotReviewAsync(owner, repo, vm.Number);
            if (success)
            {
                _notifications.Notify("Copilot review requested", $"{vm.Repository} #{vm.Number}");
            }
            else
            {
                DarkMessageBox.Show(
                    "Could not request a Copilot review for this pull request.",
                    "Request Copilot review",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning,
                    this);
            }
        }
        catch (Exception ex)
        {
            DarkMessageBox.Show(
                $"Could not request Copilot review.\n\nDetails: {ex.Message}",
                "Request Copilot review",
                MessageBoxButton.OK,
                MessageBoxImage.Error,
                this);
        }
    }

    private async Task AssignReviewerAsync(PrItemViewModel vm, string login)
    {
        if (!TrySplitRepository(vm.Repository, out var owner, out var repo))
            return;
        try
        {
            var success = await _github.RequestReviewerAsync(owner, repo, vm.Number, login);
            if (success)
            {
                UpdateRecentReviewers(login);
                _notifications.Notify("Reviewer assigned", $"{login} → {vm.Repository} #{vm.Number}");
                await ViewModel.RefreshAsync();
            }
            else
            {
                DarkMessageBox.Show(
                    $"Could not assign {login} as reviewer.",
                    "Assign reviewer",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning,
                    this);
            }
        }
        catch (Exception ex)
        {
            DarkMessageBox.Show(
                $"Could not assign reviewer.\n\nDetails: {ex.Message}",
                "Assign reviewer",
                MessageBoxButton.OK,
                MessageBoxImage.Error,
                this);
        }
    }

    private async Task RemoveReviewerAsync(PrItemViewModel vm, string login)
    {
        if (!TrySplitRepository(vm.Repository, out var owner, out var repo))
            return;
        try
        {
            var success = await _github.RemoveReviewerAsync(owner, repo, vm.Number, login);
            if (success)
                await ViewModel.RefreshAsync();
            else
                DarkMessageBox.Show(
                    $"Could not remove {login} as reviewer.",
                    "Remove reviewer",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning,
                    this);
        }
        catch (Exception ex)
        {
            DarkMessageBox.Show(
                $"Could not remove reviewer.\n\nDetails: {ex.Message}",
                "Remove reviewer",
                MessageBoxButton.OK,
                MessageBoxImage.Error,
                this);
        }
    }

    private async Task SearchAndAssignReviewerAsync(PrItemViewModel vm)
    {
        if (!TrySplitRepository(vm.Repository, out var owner, out var repo))
            return;
        var dialog = new AssignReviewerSearchWindow(_github, _settings) { Owner = this };
        if (dialog.ShowDialog() != true || dialog.SelectedLogin is not { } login)
            return;
        try
        {
            var success = await _github.RequestReviewerAsync(owner, repo, vm.Number, login);
            if (success)
            {
                UpdateRecentReviewers(login);
                _notifications.Notify("Reviewer assigned", $"{login} → {vm.Repository} #{vm.Number}");
                await ViewModel.RefreshAsync();
            }
            else
            {
                DarkMessageBox.Show(
                    $"Could not assign {login} as reviewer.",
                    "Assign reviewer",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning,
                    this);
            }
        }
        catch (Exception ex)
        {
            DarkMessageBox.Show(
                $"Could not assign reviewer.\n\nDetails: {ex.Message}",
                "Assign reviewer",
                MessageBoxButton.OK,
                MessageBoxImage.Error,
                this);
        }
    }

    private void UpdateRecentReviewers(string login)
    {
        _settings.RecentReviewers.RemoveAll(r =>
            string.Equals(r, login, StringComparison.OrdinalIgnoreCase));
        _settings.RecentReviewers.Insert(0, login);
        if (_settings.RecentReviewers.Count > 10)
            _settings.RecentReviewers.RemoveRange(10, _settings.RecentReviewers.Count - 10);
        _settings.Save();
    }

    private async Task SetPrReadyAsync(PrItemViewModel vm)
    {
        if (!vm.CanMarkAsReady)
            return;

        if (!TrySplitRepository(vm.Repository, out var owner, out var repo))
        {
            DarkMessageBox.Show(
                "Could not determine owner/repository for this PR.",
                "Mark as ready",
                MessageBoxButton.OK,
                MessageBoxImage.Warning,
                this);
            return;
        }

        try
        {
            var success = await _github.SetPrReadyAsync(owner, repo, vm.Number);
            if (success)
                await ViewModel.RefreshAsync();
            else
                DarkMessageBox.Show(
                    "Could not mark this pull request as ready.",
                    "Mark as ready",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning,
                    this);
        }
        catch (Exception ex)
        {
            DarkMessageBox.Show(
                $"Could not mark PR as ready.\n\nDetails: {ex.Message}",
                "Mark as ready",
                MessageBoxButton.OK,
                MessageBoxImage.Error,
                this);
        }
    }

    private async Task SetPrDraftAsync(PrItemViewModel vm)
    {
        if (!vm.CanConvertToDraft)
            return;

        if (!TrySplitRepository(vm.Repository, out var owner, out var repo))
        {
            DarkMessageBox.Show(
                "Could not determine owner/repository for this PR.",
                "Convert to draft",
                MessageBoxButton.OK,
                MessageBoxImage.Warning,
                this);
            return;
        }

        try
        {
            var success = await _github.SetPrDraftAsync(owner, repo, vm.Number);
            if (success)
                await ViewModel.RefreshAsync();
            else
                DarkMessageBox.Show(
                    "Could not convert this pull request to draft.",
                    "Convert to draft",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning,
                    this);
        }
        catch (Exception ex)
        {
            DarkMessageBox.Show(
                $"Could not convert PR to draft.\n\nDetails: {ex.Message}",
                "Convert to draft",
                MessageBoxButton.OK,
                MessageBoxImage.Error,
                this);
        }
    }

    private async Task EnableAutoMergeAsync(PrItemViewModel vm)
    {
        if (!vm.CanEnableAutoMerge)
            return;

        if (!TrySplitRepository(vm.Repository, out var owner, out var repo))
        {
            DarkMessageBox.Show(
                "Could not determine owner/repository for this PR.",
                "Enable auto-merge",
                MessageBoxButton.OK,
                MessageBoxImage.Warning,
                this);
            return;
        }

        try
        {
            var success = await _github.EnableAutoMergeAsync(owner, repo, vm.Number, _settings.AutoMergeMergeMethod);
            if (success)
                await ViewModel.RefreshAsync();
            else
                DarkMessageBox.Show(
                    "Could not enable auto-merge for this pull request.\n\nMake sure branch protection requires at least one check and auto-merge is allowed on this repository.",
                    "Enable auto-merge",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning,
                    this);
        }
        catch (Exception ex)
        {
            DarkMessageBox.Show(
                $"Could not enable auto-merge.\n\nDetails: {ex.Message}",
                "Enable auto-merge",
                MessageBoxButton.OK,
                MessageBoxImage.Error,
                this);
        }
    }

    private static bool TrySplitRepository(string repository, out string owner, out string repo)
    {
        owner = "";
        repo = "";
        var parts = repository.Split('/', 2, StringSplitOptions.TrimEntries);
        if (parts.Length != 2 || string.IsNullOrWhiteSpace(parts[0]) || string.IsNullOrWhiteSpace(parts[1]))
            return false;

        owner = parts[0];
        repo = parts[1];
        return true;
    }

    private void UpdateBanner_Click(object sender, MouseButtonEventArgs e)
    {
        if (ViewModel.UpdateReadyToInstall)
            ViewModel.RestartToInstallUpdate();
        else if (!ViewModel.IsDownloadingUpdate)
            _ = ViewModel.DownloadAndInstallUpdateAsync();
    }

    private async void WhatsNew_Click(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;

        try
        {
            var changelog = await ViewModel.GetUpdateChangelogAsync();
            if (changelog is not null)
            {
                ChangelogWindow.ShowForOwner(this, changelog, ViewModel.UpdateReleaseNotesUrl);
                return;
            }
        }
        catch
        {
        }

        ViewModel.ViewChangelog();
    }
}