using System.Windows.Threading;
using Rambla.Scheduling;

namespace Rambla.Wpf;

/// <summary>
/// An <see cref="IStateScheduler"/> that marshals coalesced flushes onto the WPF
/// UI thread via a <see cref="Dispatcher"/>. Flushes are posted asynchronously,
/// which is what lets background bursts coalesce into one notification pass.
/// </summary>
/// <remarks>
/// This adapter does not bound the flush rate: under a sustained feed the
/// dispatcher runs a flush as fast as it drains its queue. To cap it, wrap this
/// in a <see cref="ThrottlingStateScheduler"/> or use
/// <see cref="InstallThrottledAsDefault"/>.
/// </remarks>
public sealed class DispatcherStateScheduler : IStateScheduler
{
    private readonly Dispatcher _dispatcher;
    private readonly DispatcherPriority _priority;

    /// <param name="dispatcher">The UI dispatcher to post flushes to.</param>
    /// <param name="priority">
    /// The dispatcher priority for flushes. <see cref="DispatcherPriority.Background"/>
    /// keeps high-frequency state from starving input and rendering.
    /// </param>
    public DispatcherStateScheduler(
        Dispatcher dispatcher,
        DispatcherPriority priority = DispatcherPriority.Background)
    {
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _priority = priority;
    }

    /// <summary>Creates a scheduler bound to the dispatcher of the calling (UI) thread.</summary>
    public static DispatcherStateScheduler ForCurrent() => new(Dispatcher.CurrentDispatcher);

    /// <summary>
    /// Installs a dispatcher scheduler bound to the current thread as the process
    /// default. Call once from the UI thread during application startup.
    /// </summary>
    public static void InstallAsDefault(
        DispatcherPriority priority = DispatcherPriority.Background)
        => RamblaOptions.Default.Scheduler = new DispatcherStateScheduler(
            Dispatcher.CurrentDispatcher, priority);

    /// <summary>
    /// Creates a dispatcher scheduler for the current thread wrapped in a
    /// <see cref="ThrottlingStateScheduler"/>, and installs it as the process
    /// default. Call once from the UI thread during application startup.
    /// </summary>
    /// <param name="maxRefreshRate">
    /// The ceiling in flushes per second; defaults to
    /// <see cref="RamblaOptions.MaxRefreshRate"/>.
    /// </param>
    /// <param name="priority">The dispatcher priority for flushes.</param>
    /// <returns>
    /// The installed scheduler. It owns a timer, so the caller must dispose it at
    /// shutdown (after the writers stop).
    /// </returns>
    public static ThrottlingStateScheduler InstallThrottledAsDefault(
        int? maxRefreshRate = null,
        DispatcherPriority priority = DispatcherPriority.Background)
    {
        ThrottlingStateScheduler scheduler = new(
            new DispatcherStateScheduler(Dispatcher.CurrentDispatcher, priority), maxRefreshRate);
        RamblaOptions.Default.Scheduler = scheduler;
        return scheduler;
    }

    /// <inheritdoc />
    public void Post(Action flush) => _dispatcher.BeginInvoke(_priority, flush);
}
