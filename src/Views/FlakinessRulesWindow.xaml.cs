using System.Windows;
using PrMonitor.ViewModels;

namespace PrMonitor.Views;

public partial class FlakinessRulesWindow : Window
{
    private readonly SettingsViewModel _viewModel;

    public FlakinessRulesWindow(SettingsViewModel viewModel)
    {
        _viewModel = viewModel;
        DataContext = viewModel;
        InitializeComponent();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        DarkTitleBar.Apply(this);
    }

    private void DeleteRule_Click(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.Button btn && btn.Tag is string id)
            _viewModel.DeleteRule(id);
    }
}
