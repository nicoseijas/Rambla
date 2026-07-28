using System.Windows;
using System.Windows.Media;

namespace Rambla.Demo.MarketDashboard;

public partial class MainWindow : Window
{
    private readonly DashboardViewModel _viewModel;

    public MainWindow()
    {
        InitializeComponent();
        _viewModel = new DashboardViewModel(Dispatcher);
        DataContext = _viewModel;

        // Sample producer -> visible latency once per rendered frame.
        CompositionTarget.Rendering += OnRendering;
    }

    private void OnRendering(object? sender, EventArgs e) => _viewModel.OnRendering();

    // Start/Stop are bound to the [StateCommand]-generated commands, so there is
    // no click handler and no IsEnabled bookkeeping here.

    protected override async void OnClosed(EventArgs e)
    {
        CompositionTarget.Rendering -= OnRendering;
        await _viewModel.StopCommand.ExecuteAsync();
        base.OnClosed(e);
    }
}
