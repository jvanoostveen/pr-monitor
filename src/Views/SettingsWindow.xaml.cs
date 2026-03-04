using System.Windows;
using PrMonitor.ViewModels;

namespace PrMonitor.Views;

public partial class SettingsWindow : Window
{
    private readonly SettingsViewModel _viewModel;
    private readonly Action? _onSaved;

    public SettingsWindow(SettingsViewModel viewModel, Action? onSaved = null)
    {
        _viewModel = viewModel;
        _onSaved = onSaved;
        DataContext = viewModel;
        InitializeComponent();
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
}
