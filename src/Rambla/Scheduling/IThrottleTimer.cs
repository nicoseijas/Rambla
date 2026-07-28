using System;
using System.Diagnostics;
using System.Threading;

namespace Rambla.Scheduling;

/// <summary>
/// The time seam used by <see cref="ThrottlingStateScheduler"/>: a monotonic
/// timestamp plus a one-shot delay. Internal so tests can drive the throttling
/// window deterministically instead of sleeping.
/// </summary>
internal interface IThrottleTimer : IDisposable
{
    /// <summary>Ticks per second for <see cref="Timestamp"/>.</summary>
    long Frequency { get; }

    /// <summary>The current monotonic timestamp, in <see cref="Frequency"/> ticks.</summary>
    long Timestamp { get; }

    /// <summary>Sets the callback invoked when a scheduled delay elapses. Called once, before any <see cref="Schedule"/>.</summary>
    void Initialize(Action elapsed);

    /// <summary>
    /// Schedules the callback to run once after <paramref name="delay"/>. The
    /// scheduler arms at most one delay at a time, so implementations do not need
    /// to queue overlapping requests.
    /// </summary>
    void Schedule(TimeSpan delay);
}

/// <summary>
/// The production seam: <see cref="Stopwatch"/> for time and a
/// <see cref="Timer"/> for the delay.
/// </summary>
internal sealed class SystemThrottleTimer : IThrottleTimer
{
    private Timer? _timer;
    private Action? _elapsed;

    public long Frequency => Stopwatch.Frequency;

    public long Timestamp => Stopwatch.GetTimestamp();

    public void Initialize(Action elapsed)
    {
        _elapsed = elapsed;

        // Created idle: the scheduler only arms a delay when work is pending, so
        // an idle app pays for no periodic wake-ups at all.
        _timer = new Timer(static state => ((SystemThrottleTimer)state!)._elapsed!(), this, Timeout.Infinite, Timeout.Infinite);
    }

    public void Schedule(TimeSpan delay)
    {
        try
        {
            _timer?.Change(delay, Timeout.InfiniteTimeSpan);
        }
        catch (ObjectDisposedException)
        {
            // Raced with Dispose; the scheduler is shutting down and the release
            // it wanted is moot.
        }
    }

    public void Dispose() => Interlocked.Exchange(ref _timer, null)?.Dispose();
}
