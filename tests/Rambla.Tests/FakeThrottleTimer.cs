using Rambla.Scheduling;

namespace Rambla.Tests;

/// <summary>
/// A deterministic stand-in for the throttling scheduler's time seam: the test
/// owns the clock and decides when a scheduled delay elapses, so refresh-window
/// behaviour is asserted without sleeping.
/// </summary>
internal sealed class FakeThrottleTimer : IThrottleTimer
{
    private Action? _elapsed;
    private long _timestamp;
    private long _dueAt;

    /// <summary>One tick per millisecond keeps the arithmetic in the tests readable.</summary>
    public long Frequency => 1_000;

    public long Timestamp => Interlocked.Read(ref _timestamp);

    /// <summary>The delay currently armed, or <see langword="null"/> if none is.</summary>
    public TimeSpan? Scheduled { get; private set; }

    public int ScheduleCount { get; private set; }

    public bool Disposed { get; private set; }

    public void Initialize(Action elapsed) => _elapsed = elapsed;

    /// <summary>Whether an armed delay has already come due on the current clock.</summary>
    public bool IsDue => Scheduled is not null && Timestamp >= Interlocked.Read(ref _dueAt);

    public void Schedule(TimeSpan delay)
    {
        Scheduled = delay;
        Interlocked.Exchange(ref _dueAt, Timestamp + (long)delay.TotalMilliseconds);
        ScheduleCount++;
    }

    public void Dispose() => Disposed = true;

    /// <summary>Moves the clock forward without firing anything.</summary>
    public void Advance(TimeSpan by) => Interlocked.Add(ref _timestamp, (long)by.TotalMilliseconds);

    /// <summary>Moves the clock to the armed deadline (if not already past it) and runs the callback.</summary>
    public void FireScheduled()
    {
        if (Scheduled is null)
        {
            throw new InvalidOperationException("No delay is armed.");
        }

        Scheduled = null;
        long due = Interlocked.Read(ref _dueAt);
        if (Timestamp < due)
        {
            Interlocked.Exchange(ref _timestamp, due);
        }

        _elapsed!();
    }
}
