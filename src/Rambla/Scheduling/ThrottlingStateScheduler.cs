using System;
using System.Collections.Concurrent;
using System.Threading;

namespace Rambla.Scheduling;

/// <summary>
/// The coalescing scheduler: an <see cref="IStateScheduler"/> decorator that caps
/// how often flushes reach the UI. Posts are queued and released to the inner
/// scheduler at most <see cref="MaxRefreshRate"/> times per second; every mutation
/// that lands between two releases folds into the single pending flush each state
/// already owns. Tens of thousands of writes per second collapse into ~60
/// notification passes.
/// </summary>
/// <remarks>
/// <para>
/// Wrap the scheduler that actually reaches the UI:
/// <c>new ThrottlingStateScheduler(new DispatcherStateScheduler(dispatcher), 60)</c>.
/// The core still never references a UI framework — throttling is pure queueing
/// and timing.
/// </para>
/// <para>
/// The first post after an idle period is released <b>immediately</b> (leading
/// edge), so a sporadic update is not delayed by a full interval; only a burst is
/// paced. The rate is an upper bound: the OS timer resolution (~15 ms on Windows
/// unless the process raised it) can make the effective rate slightly lower.
/// </para>
/// <para>
/// Ownership: this type owns a timer and <b>must be disposed</b>. A
/// <see cref="RamblaState"/> never disposes its scheduler (see SEMANTICS.md §6) —
/// whoever constructs it owns it, and should dispose it after the writers stop.
/// </para>
/// <para>Threading: <see cref="Post"/> is safe from any thread.</para>
/// </remarks>
public sealed class ThrottlingStateScheduler : IStateScheduler, IDisposable
{
    private readonly IStateScheduler _inner;
    private readonly IThrottleTimer _timer;
    private readonly ConcurrentQueue<Action> _pending = new();
    private readonly long _frequency;
    private readonly long _intervalTicks;

    private long _lastRelease;
    private int _releaseArmed;
    private volatile bool _disposed;

    /// <param name="inner">The scheduler that actually marshals the flush (e.g. the WPF dispatcher adapter).</param>
    /// <param name="maxRefreshRate">
    /// The ceiling in flushes per second. Defaults to
    /// <see cref="RamblaOptions.MaxRefreshRate"/> of <see cref="RamblaOptions.Default"/>.
    /// </param>
    public ThrottlingStateScheduler(IStateScheduler inner, int? maxRefreshRate = null)
        : this(inner, maxRefreshRate ?? RamblaOptions.Default.MaxRefreshRate, new SystemThrottleTimer())
    {
    }

    internal ThrottlingStateScheduler(IStateScheduler inner, int maxRefreshRate, IThrottleTimer timer)
    {
        if (inner is null)
        {
            throw new ArgumentNullException(nameof(inner));
        }

        if (maxRefreshRate <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maxRefreshRate), maxRefreshRate, "The refresh rate must be greater than zero.");
        }

        _inner = inner;
        _timer = timer;
        _frequency = timer.Frequency;
        MaxRefreshRate = maxRefreshRate;

        // Round the interval *up*, so the rate stays a true ceiling: a truncated
        // interval would be shorter than 1/rate and release slightly more often
        // than asked for (16 ms is 62.5 Hz, not 60).
        _intervalTicks = ((_frequency + maxRefreshRate) - 1) / maxRefreshRate;

        // Start a full interval in the past so the very first post is released on
        // the spot rather than waiting out an interval nothing happened in.
        _lastRelease = timer.Timestamp - _intervalTicks;
        timer.Initialize(OnIntervalElapsed);
    }

    /// <summary>The enforced ceiling, in flushes per second.</summary>
    public int MaxRefreshRate { get; }

    /// <summary>The minimum time between two releases — the reciprocal of <see cref="MaxRefreshRate"/>.</summary>
    public TimeSpan Interval => TicksToTimeSpan(_intervalTicks);

    /// <summary>
    /// Queues <paramref name="flush"/> and releases it to the inner scheduler as
    /// soon as the refresh interval allows.
    /// </summary>
    /// <exception cref="ObjectDisposedException">The scheduler has been disposed.</exception>
    public void Post(Action flush)
    {
        if (flush is null)
        {
            throw new ArgumentNullException(nameof(flush));
        }

        // Throw rather than drop: a dropped flush leaves the posting state armed
        // forever and silently stops notifying. Throwing rolls that flag back
        // (SEMANTICS.md §1) and tells the caller its scheduler is gone.
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(ThrottlingStateScheduler));
        }

        _pending.Enqueue(flush);

        // Re-check: a Dispose that raced this post has already emptied the queue
        // and will never release again, so the flush just enqueued would be
        // silently lost. Tell the writer instead.
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(ThrottlingStateScheduler));
        }

        TryArmRelease(propagatePostFailure: true);
    }

    /// <summary>
    /// Stops the timer. Flushes still queued are dropped, so stop the writers
    /// first if the last frame matters.
    /// </summary>
    public void Dispose()
    {
        _disposed = true;
        _timer.Dispose();

        while (_pending.TryDequeue(out _))
        {
            // Drain without running: the UI this scheduler fed is going away.
        }
    }

    /// <summary>
    /// Claims the right to release the queue and either releases now or arms the
    /// timer for the remainder of the interval. At most one release is in flight,
    /// which is what makes a burst collapse into one pass.
    /// </summary>
    private void TryArmRelease(bool propagatePostFailure)
    {
        if (Interlocked.CompareExchange(ref _releaseArmed, 1, 0) != 0)
        {
            return;
        }

        if (_disposed)
        {
            return;
        }

        long remaining = _intervalTicks - (_timer.Timestamp - Interlocked.Read(ref _lastRelease));
        if (remaining <= 0)
        {
            Release(propagatePostFailure);
        }
        else
        {
            _timer.Schedule(TicksToTimeSpan(remaining));
        }
    }

    private void OnIntervalElapsed()
    {
        // A rejected post cannot be propagated from here: this runs on a timer
        // thread, where an escaping exception takes the process down and there is
        // no writer to report it to. Roll the flag back so the next post retries.
        Release(propagatePostFailure: false);
    }

    private void Release(bool propagatePostFailure)
    {
        if (_disposed)
        {
            return;
        }

        // An inline inner scheduler runs the drain inside Post, so "the post
        // failed" and "a flush threw" arrive through the same call. The filter
        // separates them: only a rejected post is handled here — an exception
        // from a flush is the subscriber's, and stays fail-fast (SEMANTICS §1).
        bool draining = false;
        try
        {
            _inner.Post(() =>
            {
                draining = true;
                Drain();
            });
        }
        catch when (!draining)
        {
            Volatile.Write(ref _releaseArmed, 0);

            if (propagatePostFailure)
            {
                throw;
            }
        }
    }

    /// <summary>
    /// Runs on the inner scheduler's context (the UI thread under a dispatcher
    /// adapter) and drains the flushes queued up to this point.
    /// </summary>
    private void Drain()
    {
        Interlocked.Exchange(ref _lastRelease, _timer.Timestamp);

        // Disarm *before* draining, and before snapshotting the budget: a post
        // that races this drain then either lands inside the snapshot or arms the
        // next release. Disarming afterwards would let such a post see an armed
        // release that has already passed it by — and its flush would never run.
        Volatile.Write(ref _releaseArmed, 0);

        // Bound the pass by the backlog at entry so flushes re-posted while
        // draining wait for the next interval instead of extending this one
        // unbounded — the UI thread gets its frame back.
        int budget = _pending.Count;

        try
        {
            while (budget-- > 0 && _pending.TryDequeue(out Action? flush))
            {
                flush();
            }
        }
        catch
        {
            // A subscriber that throws aborts the pass and propagates (SEMANTICS.md
            // §1), but it must not strand the flushes queued behind it: re-arm so
            // they are released in the next interval.
            if (!_pending.IsEmpty)
            {
                TryArmRelease(propagatePostFailure: false);
            }

            throw;
        }
    }

    private TimeSpan TicksToTimeSpan(long ticks) => TimeSpan.FromSeconds(ticks / (double)_frequency);
}
