using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Threading.Tasks;
using System.Windows.Threading;
using Rambla.Demo.MarketDashboard.Diagnostics;
using Rambla.Demo.MarketDashboard.Feed;
using Rambla.Demo.MarketDashboard.Model;
using Rambla.Demo.MarketDashboard.Scheduling;
using Rambla.Scheduling;
using Rambla.Wpf;

namespace Rambla.Demo.MarketDashboard;

/// <summary>
/// Orchestrates the demo: builds the rows for the selected mode, runs the feed,
/// and republishes per-second metrics to the UI once a second. It dogfoods
/// <see cref="RamblaState"/> for its own stat properties (updated on the UI
/// thread, so the immediate scheduler is exactly right).
/// </summary>
public sealed partial class DashboardViewModel : RamblaState
{
    private static readonly double TicksToMs = 1000.0 / Stopwatch.Frequency;

    private readonly Dispatcher _dispatcher;
    private readonly DemoMetrics _metrics = new();
    private readonly LatencyProbe _probe = new();
    private readonly List<ISymbolRow> _tracked = new();
    private readonly DispatcherTimer _statsTimer;
    private readonly DispatcherTimer _lagProbe;

    private SyntheticFeed? _feed;
    private ThrottlingStateScheduler? _throttled;
    private ISymbolRow? _canary;

    private long _lagLastTicks;
    private double _lagMaxMs;

    private int _symbolCount = 250;
    private int _targetRate = 50_000;
    private int _refreshHz = 60;
    private FeedMode _mode = FeedMode.RamblaCoalesced;

    private bool _running;
    private string _statusText = "Idle. Choose a mode and press Start.";
    private double _incomingPerSec;
    private double _notificationsPerSec;
    private double _postsPerSec;
    private double _coalescingPercent;
    private double _flushP50Ms;
    private double _flushP95Ms;
    private double _flushP99Ms;
    private double _uiLagMaxMs;
    private double _latencyP50Ms;
    private double _latencyP95Ms;
    private double _latencyP99Ms;

    public DashboardViewModel(Dispatcher dispatcher)
        : base(ImmediateStateScheduler.Instance)
    {
        _dispatcher = dispatcher;

        _statsTimer = new DispatcherTimer(
            TimeSpan.FromSeconds(1), DispatcherPriority.Send, OnStatsTick, dispatcher);
        _statsTimer.Start();

        // Background-priority probe: how late does a low-priority callback run
        // when the feed is hammering the dispatcher? That lateness is UI lag.
        _lagLastTicks = Stopwatch.GetTimestamp();
        _lagProbe = new DispatcherTimer(
            TimeSpan.FromMilliseconds(16), DispatcherPriority.Background, OnLagTick, dispatcher);
        _lagProbe.Start();
    }

    public ObservableCollection<ISymbolRow> Rows { get; } = new();

    public Array Modes => Enum.GetValues<FeedMode>();

    public int SymbolCount { get => _symbolCount; set => SetField(ref _symbolCount, Math.Clamp(value, 1, 5000)); }

    public int TargetRate { get => _targetRate; set => SetField(ref _targetRate, Math.Clamp(value, 1, 5_000_000)); }

    public int RefreshHz { get => _refreshHz; set => SetField(ref _refreshHz, Math.Clamp(value, 1, 240)); }

    public FeedMode Mode { get => _mode; set => SetField(ref _mode, value); }

    public bool Running { get => _running; private set => SetField(ref _running, value); }

    public string StatusText { get => _statusText; private set => SetField(ref _statusText, value); }

    public double IncomingPerSec { get => _incomingPerSec; private set => SetField(ref _incomingPerSec, value); }

    public double NotificationsPerSec { get => _notificationsPerSec; private set => SetField(ref _notificationsPerSec, value); }

    public double PostsPerSec { get => _postsPerSec; private set => SetField(ref _postsPerSec, value); }

    public double CoalescingPercent { get => _coalescingPercent; private set => SetField(ref _coalescingPercent, value); }

    public double FlushP50Ms { get => _flushP50Ms; private set => SetField(ref _flushP50Ms, value); }

    public double FlushP95Ms { get => _flushP95Ms; private set => SetField(ref _flushP95Ms, value); }

    public double FlushP99Ms { get => _flushP99Ms; private set => SetField(ref _flushP99Ms, value); }

    public double UiLagMaxMs { get => _uiLagMaxMs; private set => SetField(ref _uiLagMaxMs, value); }

    public double LatencyP50Ms { get => _latencyP50Ms; private set => SetField(ref _latencyP50Ms, value); }

    public double LatencyP95Ms { get => _latencyP95Ms; private set => SetField(ref _latencyP95Ms, value); }

    public double LatencyP99Ms { get => _latencyP99Ms; private set => SetField(ref _latencyP99Ms, value); }

    /// <summary>Called once per rendered frame to sample producer → visible latency from the canary row.</summary>
    public void OnRendering()
    {
        if (_running && _canary is not null)
        {
            _probe.Observe(_canary.Last);
        }
    }

    /// <summary>
    /// Generates StartCommand / IsStarting / StartError / CancelStartCommand. The
    /// command disables itself while it runs, which is why the buttons need no
    /// IsEnabled juggling in code-behind.
    /// </summary>
    [StateCommand]
    private async Task StartAsync()
    {
        await StopAsync().ConfigureAwait(true);

        BuildRows();
        _metrics.Reset();
        _probe.Reset();

        ISymbolRow[] rows = _tracked.ToArray();
        _feed = new SyntheticFeed(rows, _metrics, _targetRate, _probe);
        _feed.Start();

        Running = true;
        StatusText = $"Running {_mode} · {_symbolCount} symbols · target {_targetRate:N0} quotes/s"
            + (_mode == FeedMode.RamblaCoalesced ? $" · {_refreshHz} Hz" : string.Empty);
    }

    /// <summary>Generates StopCommand / IsStopping / StopError / CancelStopCommand.</summary>
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

    private void BuildRows()
    {
        foreach (ISymbolRow row in _tracked)
        {
            row.PropertyChanged -= OnRowNotification;
        }

        _tracked.Clear();
        Rows.Clear();

        IStateScheduler? scheduler = _mode switch
        {
            FeedMode.RamblaImmediate => new MeteringScheduler(ImmediateStateScheduler.Instance, _metrics),
            FeedMode.RamblaCoalesced => BuildCoalescingScheduler(),
            _ => null,
        };

        for (int i = 0; i < _symbolCount; i++)
        {
            string symbol = $"SYM{i:D4}";
            ISymbolRow row = _mode == FeedMode.Naive
                ? new NaiveSymbolRow(symbol)
                : new RamblaSymbolRow(symbol, scheduler!);

            row.PropertyChanged += OnRowNotification;
            _tracked.Add(row);
            Rows.Add(row);
        }

        _canary = _tracked.Count > 0 ? _tracked[0] : null;
    }

    private IStateScheduler BuildCoalescingScheduler()
    {
        // The library's own throttling scheduler, wrapping the WPF adapter: this
        // is exactly what a consumer writes (or gets from InstallThrottledAsDefault).
        _throttled = new ThrottlingStateScheduler(new DispatcherStateScheduler(_dispatcher), _refreshHz);
        return new MeteringScheduler(_throttled, _metrics);
    }

    private void OnRowNotification(object? sender, PropertyChangedEventArgs e) => _metrics.OnNotification();

    private void OnStatsTick(object? sender, EventArgs e)
    {
        MetricsSample sample = _metrics.Sample();
        IncomingPerSec = sample.IncomingPerSecond;
        NotificationsPerSec = sample.NotificationsPerSecond;
        PostsPerSec = sample.SchedulerPostsPerSecond;
        CoalescingPercent = sample.CoalescingRatio * 100.0;
        FlushP50Ms = sample.FlushP50Ms;
        FlushP95Ms = sample.FlushP95Ms;
        FlushP99Ms = sample.FlushP99Ms;

        UiLagMaxMs = _lagMaxMs;
        _lagMaxMs = 0;

        LatencyP50Ms = _probe.P50Ms;
        LatencyP95Ms = _probe.P95Ms;
        LatencyP99Ms = _probe.P99Ms;
    }

    private void OnLagTick(object? sender, EventArgs e)
    {
        long now = Stopwatch.GetTimestamp();
        double elapsedMs = (now - _lagLastTicks) * TicksToMs;
        _lagLastTicks = now;

        // Anything beyond the 16 ms cadence is dispatcher lateness = UI lag.
        double lag = elapsedMs - 16.0;
        if (lag > _lagMaxMs)
        {
            _lagMaxMs = lag;
        }
    }
}
