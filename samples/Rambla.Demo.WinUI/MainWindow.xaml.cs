using Microsoft.UI.Xaml;

namespace Rambla.Demo.WinUI;

public sealed partial class MainWindow : Window
{
    private readonly DashboardViewModel _viewModel;

    public MainWindow()
    {
        InitializeComponent();
        _viewModel = new DashboardViewModel(DispatcherQueue);
        Root.DataContext = _viewModel;
        Closed += OnClosed;
    }

    private async void OnClosed(object sender, WindowEventArgs args)
    {
        Closed -= OnClosed;
        await _viewModel.StopCommand.ExecuteAsync();
        _viewModel.Dispose();
    }
}
