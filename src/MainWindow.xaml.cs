using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using PrMonitor.Services;
using PrMonitor.Settings;
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
    private readonly AppSettings _settings;
    private readonly GitHubService _github;
    private readonly NotificationService _notifications;
    private DoubleAnimation? _spinAnimation;
    private bool _userMoved;
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

    public MainWindow(MainViewModel viewModel, AppSettings settings, GitHubService github, NotificationService notifications)
    {
        ViewModel = viewModel;
        _settings = settings;
        _github = github;
        _notifications = notifications;
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
            RestoreStartupPlacement();
            Microsoft.Win32.SystemEvents.DisplaySettingsChanging += OnDisplaySettingsChanging;
            Microsoft.Win32.SystemEvents.DisplaySettingsChanged  += OnDisplaySettingsChanged;
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
        PersistWindowState(isVisible: true);
    }

    /// <summary>
    /// Hide the window and persist hidden state.
    /// </summary>
    public void HideToTray()
    {
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

    private void RestoreStartupPlacement()
    {
        if (_settings.MainWindowLeft is not double savedLeft || _settings.MainWindowTop is not double savedTop)
        {
            AlignToBottomRight();
            return;
        }

        var width = ActualWidth > 0 ? ActualWidth : Width;
        var height = ActualHeight > 0 ? ActualHeight : Height;
        var (left, top) = ClampToBestWorkArea(savedLeft, savedTop, width, height);
        SetWindowPosition(left, top);
        _userMoved = true;
        if (_settings.MainWindowSnappedCorner is { } cornerStr
            && Enum.TryParse<SnapCorner>(cornerStr, out var restoredCorner))
            _snappedCorner = restoredCorner;
        PersistWindowPosition();
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

    private static double ComputeOverlapArea(Rect a, Rect b)
    {
        var x1 = Math.Max(a.Left, b.Left);
        var y1 = Math.Max(a.Top, b.Top);
        var x2 = Math.Min(a.Right, b.Right);
        var y2 = Math.Min(a.Bottom, b.Bottom);
        var w = Math.Max(0, x2 - x1);
        var h = Math.Max(0, y2 - y1);
        return w * h;
    }

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
            _settings.MainWindowLeft = Left;
            _settings.MainWindowTop = Top;
            _settings.MainWindowSnappedCorner = _snappedCorner == SnapCorner.None ? null : _snappedCorner.ToString();
            if (saveToDisk)
                _settings.Save();
        }
    }

    private void PersistWindowState(bool isVisible)
    {
        _settings.MainWindowVisible = isVisible;
        _settings.Save();
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
        var width = ActualWidth > 0 ? ActualWidth : Width;
        var height = ActualHeight > 0 ? ActualHeight : Height;
        var (left, top) = ClampToBestWorkArea(Left, Top, width, height);
        if (Math.Abs(left - Left) > 0.5 || Math.Abs(top - Top) > 0.5)
            SetWindowPosition(left, top);

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
            AlignToBottomRight(width, height);
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
            SetWindowPosition(l, t);
        }
        else
        {
            // No snap: clamp the pre-change position to the best available work area.
            var (l, t) = ClampToBestWorkArea(_preChangeLeft, _preChangeTop, width, height);
            SetWindowPosition(l, t);
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
        if (!_isProgrammaticMove && !_displayChangePending)
        {
            _userMoved = true;
            PersistWindowPosition(saveToDisk: false);
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
        HideToTray();
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

    private async void PrRow_RerunFailedJobs_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.MenuItem { Tag: PrItemViewModel vm })
            return;

        if (!vm.CanRerunFailedJobs)
            return;

        if (!TrySplitRepository(vm.Repository, out var owner, out var repo))
        {
            System.Windows.MessageBox.Show("Could not determine owner/repository for this PR.", "Rerun failed jobs",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (string.IsNullOrWhiteSpace(vm.HeadCommitSha))
        {
            System.Windows.MessageBox.Show("No head commit SHA found for this PR.", "Rerun failed jobs",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        try
        {
            var failedRunIds = await _github.FetchFailedRunIdsAsync(owner, repo, vm.HeadCommitSha);
            if (failedRunIds.Count == 0)
            {
                System.Windows.MessageBox.Show("No failed workflow runs were found for this PR commit.", "Rerun failed jobs",
                    MessageBoxButton.OK, MessageBoxImage.Information);
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
                System.Windows.MessageBox.Show(
                    "Failed to trigger rerun for the failed workflow runs.",
                    "Rerun failed jobs",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(
                $"Could not rerun failed jobs.\n\nDetails: {ex.Message}",
                "Rerun failed jobs",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
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
        ViewModel.OpenUpdateRelease();
    }
}