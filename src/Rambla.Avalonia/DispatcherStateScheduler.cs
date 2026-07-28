using System;
using Avalonia.Threading;
using Rambla.Scheduling;

namespace Rambla.Avalonia;

/// <summary>
/// An <see cref="IStateScheduler"/> that marshals coalesced flushes onto the
/// Avalonia UI thread via an <see cref="IDispatcher"/>. Flushes are posted
/// asynchronously, which is what lets background bursts coalesce into one
/// notification pass.
/// </summary>
/// <remarks>
/// This adapter does not bound the flush rate: under a sustained feed the
/// dispatcher runs a flush as fast as it drains its queue. To cap it, wrap this
/// in a <see cref="ThrottlingStateScheduler"/> or use
/// <see cref="InstallThrottledAsDefault"/>.
/// </remarks>
public sealed class DispatcherStateScheduler : IStateScheduler
{
    private readonly IDispatcher _dispatcher;
    private readonly DispatcherPriority _priority;

    /// <param name="dispatcher">The UI dispatcher to post flushes to.</param>
    /// <param name="priority">
    /// The dispatcher priority for flushes. Defaults to
    /// <see cref="DispatcherPriority.Background"/>, which keeps high-frequency
    /// state from starving input and rendering. (It is a nullable parameter
    /// because <see cref="DispatcherPriority"/> is a struct and cannot be a
    /// default argument.)
    /// </param>
    public DispatcherStateScheduler(IDispatcher dispatcher, DispatcherPriority? priority = null)
    {
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _priority = priority ?? DispatcherPriority.Background;
    }

    /// <summary>Creates a scheduler bound to the Avalonia UI thread's dispatcher.</summary>
    public static DispatcherStateScheduler ForUIThread(DispatcherPriority? priority = null)
        => new(Dispatcher.UIThread, priority);

    /// <summary>
    /// Installs a scheduler bound to the Avalonia UI thread as the process
    /// default. Call once during application startup.
    /// </summary>
    public static void InstallAsDefault(DispatcherPriority? priority = null)
        => RamblaOptions.Default.Scheduler = ForUIThread(priority);

    /// <summary>
    /// Installs the UI-thread scheduler wrapped in a
    /// <see cref="ThrottlingStateScheduler"/> as the process default, so flushes
    /// are capped at <paramref name="maxRefreshRate"/> per second (defaulting to
    /// <see cref="RamblaOptions.MaxRefreshRate"/>).
    /// </summary>
    /// <returns>
    /// The installed scheduler. It owns a timer, so the caller must dispose it at
    /// shutdown (after the writers stop).
    /// </returns>
    public static ThrottlingStateScheduler InstallThrottledAsDefault(
        int? maxRefreshRate = null,
        DispatcherPriority? priority = null)
    {
        ThrottlingStateScheduler scheduler = new(ForUIThread(priority), maxRefreshRate);
        RamblaOptions.Default.Scheduler = scheduler;
        return scheduler;
    }

    /// <inheritdoc />
    public void Post(Action flush) => _dispatcher.Post(flush, _priority);
}
