using Microsoft.UI.Xaml;
using Rambla.WinUI;
using Rambla.WinUI.Virtualization;

namespace Rambla.Demo.WinUI.Virtualization;

public sealed partial class MainWindow : Window
{
    private readonly VirtualizingList<QuoteRow> _rows;

    public MainWindow()
    {
        InitializeComponent();
        _rows = new VirtualizingList<QuoteRow>(
            new SyntheticQuoteRangeSource(500_000),
            static index => QuoteRow.Loading(index),
            new DispatcherStateScheduler(DispatcherQueue),
            prefetchBefore: 200,
            prefetchAfter: 200,
            maximumCachedItems: 420);
        Quotes.ItemsSource = _rows;
        Closed += OnClosed;
        RefreshMetrics();
    }

    private void OnRefreshMetrics(object sender, RoutedEventArgs args) => RefreshMetrics();

    private void OnClosed(object sender, WindowEventArgs args)
    {
        Closed -= OnClosed;
        _rows.Dispose();
    }

    private void RefreshMetrics()
        => CacheMetrics.Text = $"Logical rows: {_rows.Count:N0} · cached now: {_rows.CachedItemCount:N0} / {_rows.MaximumCachedItems:N0}";
}
