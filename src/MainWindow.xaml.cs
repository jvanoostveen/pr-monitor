using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using PrBot.ViewModels;

namespace PrBot;

/// <summary>
/// Floating window that shows PR lists. Positions itself above the
/// taskbar (bottom-right) and hides on deactivation.
/// </summary>
public partial class MainWindow : Window
{
    public MainViewModel ViewModel { get; }
    private DoubleAnimation? _spinAnimation;
    private bool _userMoved;
    private bool _isProgrammaticMove;

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

        Loaded += (_, _) => AlignToBottomRight();
    }

    /// <summary>
    /// Show the window. Snaps to bottom-right unless the user has manually moved it.
    /// </summary>
    public void ShowAtTray()
    {
        if (!_userMoved) AlignToBottomRight();
        Show();
        Activate();
    }

    private void SetWindowPosition(double left, double top)
    {
        _isProgrammaticMove = true;
        Left = left;
        Top = top;
        _isProgrammaticMove = false;
    }

    private void AlignToBottomRight(double? width = null, double? height = null)
    {
        var wa = SystemParameters.WorkArea;
        double w = width  ?? (ActualWidth  > 0 ? ActualWidth  : Width);
        double h = height ?? (ActualHeight > 0 ? ActualHeight : 0);
        SetWindowPosition(wa.Right - w - 12, wa.Bottom - h - 12);
    }

    private void EnsureFullyVisible()
    {
        var wa = SystemParameters.WorkArea;
        double left = Left, top = Top;
        if (left + ActualWidth  > wa.Right)  left = wa.Right  - ActualWidth;
        if (top  + ActualHeight > wa.Bottom) top  = wa.Bottom - ActualHeight;
        if (left < wa.Left) left = wa.Left;
        if (top  < wa.Top)  top  = wa.Top;
        if (Math.Abs(left - Left) > 0.5 || Math.Abs(top - Top) > 0.5)
            SetWindowPosition(left, top);
    }

    protected override void OnLocationChanged(EventArgs e)
    {
        base.OnLocationChanged(e);
        if (!_isProgrammaticMove)
            _userMoved = true;
    }

    private void Window_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_userMoved)
            EnsureFullyVisible();
        else
            AlignToBottomRight(e.NewSize.Width, e.NewSize.Height);
    }

    private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        DragMove();
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
}