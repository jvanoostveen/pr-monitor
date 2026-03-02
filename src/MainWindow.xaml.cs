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
    private bool _pinned;
    private DoubleAnimation? _spinAnimation;

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
    }

    /// <summary>
    /// Show the window positioned just above the system tray area.
    /// </summary>
    public void ShowAtTray()
    {
        PositionNearTray();
        Show();
        Activate();
    }

    private void PositionNearTray()
    {
        var workArea = SystemParameters.WorkArea;
        Left = workArea.Right - Width - 8;
        Top = workArea.Bottom - ActualHeight - 8;

        // ActualHeight may be 0 before first render; handle with Loaded
        if (ActualHeight == 0)
        {
            Loaded += (_, _) =>
            {
                Left = workArea.Right - ActualWidth - 8;
                Top = workArea.Bottom - ActualHeight - 8;
            };
        }
    }

    private void Window_Deactivated(object sender, EventArgs e)
    {
        // When pinned, keep the window visible
        if (_pinned) return;
        Hide();
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

    private void RefreshButton_Click(object sender, MouseButtonEventArgs e)
    {
        _ = ViewModel.RefreshAsync();
    }

    private void PinButton_Click(object sender, MouseButtonEventArgs e)
    {
        _pinned = !_pinned;
        Topmost = true; // always topmost, but pinned controls hide-on-deactivate
        PinIcon.Foreground = _pinned
            ? new System.Windows.Media.SolidColorBrush(
                (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#58A6FF"))
            : new System.Windows.Media.SolidColorBrush(
                (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#6E7681"));
        PinButton.ToolTip = _pinned ? "Unpin window" : "Pin window (stay on top)";
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
        ViewModel.OpenMyPrsInBrowser();
    }

    private void ReviewHeader_Click(object sender, MouseButtonEventArgs e)
    {
        ViewModel.OpenReviewsInBrowser();
    }
}