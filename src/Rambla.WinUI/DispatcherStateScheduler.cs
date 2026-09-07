using Microsoft.UI.Dispatching;
using Rambla.Scheduling;

namespace Rambla.WinUI;

/// <summary>
/// An <see cref="IStateScheduler"/> that marshals coalesced flushes onto a WinUI
/// UI thread through its <see cref="DispatcherQueue"/>. Flushes are posted
/// asynchronously, which lets background bursts coalesce into one notification
/// pass.
/// </summary>
/// <remarks>
/// This adapter does not bound the flush rate: under a sustained feed the
/// dispatcher runs a flush as fast as it drains its queue. To cap it, wrap this
/// in a <see cref="ThrottlingStateScheduler"/> or use
/// <see cref="InstallThrottledAsDefault"/>.
/// </remarks>
public sealed class DispatcherStateScheduler : IStateScheduler
{
    private readonly Func<DispatcherQueuePriority, Action, bool> _tryEnqueue;
    private readonly DispatcherQueuePriority _priority;

    /// <param name="dispatcherQueue">The WinUI dispatcher queue that owns the UI thread.</param>
    /// <param name="priority">
    /// The dispatcher priority for flushes. <see cref="DispatcherQueuePriority.Low"/>
    /// keeps high-frequency state from starving input and rendering.
    /// </param>
    public DispatcherStateScheduler(
        DispatcherQueue dispatcherQueue,
        DispatcherQueuePriority priority = DispatcherQueuePriority.Low)
    {
        ArgumentNullException.ThrowIfNull(dispatcherQueue);

        _tryEnqueue = (queuePriority, flush) => dispatcherQueue.TryEnqueue(queuePriority, () => flush());
        _priority = priority;
    }

    internal DispatcherStateScheduler(
        Func<DispatcherQueuePriority, Action, bool> tryEnqueue,
        DispatcherQueuePriority priority = DispatcherQueuePriority.Low)
    {
        _tryEnqueue = tryEnqueue ?? throw new ArgumentNullException(nameof(tryEnqueue));
        _priority = priority;
    }

    /// <summary>
    /// Creates a scheduler bound to the dispatcher queue of the calling UI
    /// thread.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// No dispatcher queue is associated with the current thread.
    /// </exception>
    public static DispatcherStateScheduler ForCurrent(
        DispatcherQueuePriority priority = DispatcherQueuePriority.Low)
    {
        DispatcherQueue dispatcherQueue = DispatcherQueue.GetForCurrentThread()
            ?? throw new InvalidOperationException(
                "No DispatcherQueue is available on the current thread. " +
                "Call this from the WinUI UI thread or pass the window's DispatcherQueue explicitly.");

        return new DispatcherStateScheduler(dispatcherQueue, priority);
    }

    /// <summary>
    /// Installs a scheduler bound to the current WinUI UI thread as the process
    /// default. Call once during application startup.
    /// </summary>
    public static void InstallAsDefault(
        DispatcherQueuePriority priority = DispatcherQueuePriority.Low)
        => RamblaOptions.Default.Scheduler = ForCurrent(priority);

    /// <summary>
    /// Creates a scheduler for the current WinUI UI thread, wraps it in a
    /// <see cref="ThrottlingStateScheduler"/>, and installs it as the process
    /// default.
    /// </summary>
    /// <param name="maxRefreshRate">
    /// The ceiling in flushes per second; defaults to
    /// <see cref="RamblaOptions.MaxRefreshRate"/>.
    /// </param>
    /// <param name="priority">The dispatcher priority for flushes.</param>
    /// <returns>
    /// The installed scheduler. It owns a timer, so the caller must dispose it at
    /// shutdown after the writers stop.
    /// </returns>
    public static ThrottlingStateScheduler InstallThrottledAsDefault(
        int? maxRefreshRate = null,
        DispatcherQueuePriority priority = DispatcherQueuePriority.Low)
    {
        ThrottlingStateScheduler scheduler = new(ForCurrent(priority), maxRefreshRate);
        RamblaOptions.Default.Scheduler = scheduler;
        return scheduler;
    }

    /// <inheritdoc />
    /// <exception cref="InvalidOperationException">
    /// The dispatcher queue is shutting down and rejected the flush.
    /// </exception>
    public void Post(Action flush)
    {
        ArgumentNullException.ThrowIfNull(flush);

        // RamblaState rolls its armed-flush flag back when Post throws. Returning
        // normally after TryEnqueue=false would instead strand that state forever.
        if (!_tryEnqueue(_priority, flush))
        {
            throw new InvalidOperationException(
                "The WinUI DispatcherQueue rejected the flush because it is shutting down.");
        }
    }
}
