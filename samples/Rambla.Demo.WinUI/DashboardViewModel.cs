using System.Collections.ObjectModel;
using System.ComponentModel;
using Microsoft.UI.Dispatching;
using Rambla.Demo.MarketDashboard.Diagnostics;
using Rambla.Demo.MarketDashboard.Feed;
using Rambla.Demo.MarketDashboard.Model;
using Rambla.Demo.MarketDashboard.Scheduling;
using Rambla.Scheduling;
using Rambla.WinUI;

namespace Rambla.Demo.WinUI;

/// <summary>
/// A real WinUI 3 workload: a background quote feed writes into Rambla rows while
/// the Windows App SDK dispatcher delivers no more than the selected refresh rate.
/// </summary>
public sealed partial class DashboardViewModel : RamblaState, IDisposable
{
    private readonly DispatcherQueue _dispatcherQueue;
    private readonly DemoMetrics _metrics = new();
    private readonly List<RamblaSymbolRow> _tracked = new();
    private readonly DispatcherQueueTimer _statsTimer;

    private SyntheticFeed? _feed;
    private ThrottlingStateScheduler? _throttled;
    private bool _disposed;

    private int _symbolCount = 250;
    private int _targetRate = 50_000;
    private int _refreshHz = 60;
    private bool _running;
    private string _statusText = "Idle. Press Start to run the WinUI dispatcher demo.";
    private double _incomingPerSec;
    private double _notificationsPerSec;
    private double _postsPerSec;
    private double _coalescingPercent;

    public DashboardViewModel(DispatcherQueue dispatcherQueue)
        : base(ImmediateStateScheduler.Instance)
    {
        _dispatcherQueue = dispatcherQueue ?? throw new ArgumentNullException(nameof(dispatcherQueue));
        _statsTimer = _dispatcherQueue.CreateTimer();
        _statsTimer.Interval = TimeSpan.FromSeconds(1);
        _statsTimer.Tick += OnStatsTick;
        _statsTimer.Start();
    }

    public ObservableCollection<RamblaSymbolRow> Rows { get; } = new();

    public int SymbolCount
    {
        get => _symbolCount;
        set => SetField(ref _symbolCount, Math.Clamp(value, 1, 5_000));
    }

    public int TargetRate
    {
        get => _targetRate;
        set => SetField(ref _targetRate, Math.Clamp(value, 1, 5_000_000));
    }

    public int RefreshHz
    {
        get => _refreshHz;
        set => SetField(ref _refreshHz, Math.Clamp(value, 1, 240));
    }

    public bool Running { get => _running; private set => SetField(ref _running, value); }

    public string StatusText { get => _statusText; private set => SetField(ref _statusText, value); }

    public double IncomingPerSec { get => _incomingPerSec; private set => SetField(ref _incomingPerSec, value); }

    public double NotificationsPerSec { get => _notificationsPerSec; private set => SetField(ref _notificationsPerSec, value); }

    public double PostsPerSec { get => _postsPerSec; private set => SetField(ref _postsPerSec, value); }

    public double CoalescingPercent { get => _coalescingPercent; private set => SetField(ref _coalescingPercent, value); }

    public string IncomingPerSecText => IncomingPerSec.ToString("N0");

    public string NotificationsPerSecText => NotificationsPerSec.ToString("N0");

    public string PostsPerSecText => PostsPerSec.ToString("N0");

    public string CoalescingPercentText => CoalescingPercent.ToString("N2") + "%";

    [StateCommand]
    private async Task StartAsync()
    {
        await StopAsync().ConfigureAwait(true);

        BuildRows();
        _metrics.Reset();
        _feed = new SyntheticFeed(_tracked.Cast<ISymbolRow>().ToArray(), _metrics, TargetRate);
        _feed.Start();

        Running = true;
        StatusText = $"Running · {SymbolCount} symbols · target {TargetRate:N0} quotes/s · {RefreshHz} Hz";
    }

    [StateCommand]
    private async Task StopAsync()
    {
        if (_feed is not null)
        {
            await _feed.StopAsync().ConfigureAwait(true);
            _feed = null;
        }

        _throttled?.Dispose();
        _throttled = null;

        if (Running)
        {
            Running = false;
            StatusText = "Stopped.";
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _statsTimer.Stop();
        _statsTimer.Tick -= OnStatsTick;
        _throttled?.Dispose();
    }

    private void BuildRows()
    {
        foreach (RamblaSymbolRow row in _tracked)
        {
            row.PropertyChanged -= OnRowNotification;
        }

        _tracked.Clear();
        Rows.Clear();

        _throttled = new ThrottlingStateScheduler(
            new DispatcherStateScheduler(_dispatcherQueue), RefreshHz);
        IStateScheduler scheduler = new MeteringScheduler(_throttled, _metrics);

        for (int i = 0; i < SymbolCount; i++)
        {
            var row = new RamblaSymbolRow($"SYM{i:D4}", scheduler);
            row.PropertyChanged += OnRowNotification;
            _tracked.Add(row);
            Rows.Add(row);
        }
    }

    private void OnRowNotification(object? sender, PropertyChangedEventArgs e) => _metrics.OnNotification();

    private void OnStatsTick(DispatcherQueueTimer sender, object args)
    {
        MetricsSample sample = _metrics.Sample();
        using (BeginUpdate())
        {
            IncomingPerSec = sample.IncomingPerSecond;
            NotificationsPerSec = sample.NotificationsPerSecond;
            PostsPerSec = sample.SchedulerPostsPerSecond;
            CoalescingPercent = sample.CoalescingRatio * 100.0;

            MarkDirty(nameof(IncomingPerSecText));
            MarkDirty(nameof(NotificationsPerSecText));
            MarkDirty(nameof(PostsPerSecText));
            MarkDirty(nameof(CoalescingPercentText));
        }
    }
}
