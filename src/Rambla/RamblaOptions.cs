using Rambla.Scheduling;

namespace Rambla;

/// <summary>
/// Global configuration for <see cref="RamblaState"/> instances that do not
/// receive an explicit scheduler.
/// </summary>
public sealed class RamblaOptions
{
    private int _maxRefreshRate = 60;

    /// <summary>The ambient options used when a state is created without an explicit scheduler.</summary>
    public static RamblaOptions Default { get; } = new();

    /// <summary>
    /// The scheduler that marshals coalesced flushes to the UI. Defaults to the
    /// synchronous <see cref="ImmediateStateScheduler"/>; UI adapters replace this
    /// at startup (e.g. <c>RamblaOptions.Default.Scheduler = DispatcherStateScheduler.ForCurrent();</c>).
    /// </summary>
    public IStateScheduler Scheduler { get; set; } = ImmediateStateScheduler.Instance;

    /// <summary>
    /// Upper bound, in flushes per second, applied by a
    /// <see cref="ThrottlingStateScheduler"/> constructed without an explicit
    /// rate. Read at construction time, so changing it later does not re-pace a
    /// scheduler that already exists. Plain schedulers
    /// (<see cref="ImmediateStateScheduler"/>, <see cref="SynchronizationContextStateScheduler"/>,
    /// the dispatcher adapter) do not throttle — wrap them to opt in.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is not greater than zero.</exception>
    public int MaxRefreshRate
    {
        get => _maxRefreshRate;
        set
        {
            if (value <= 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(value), value, "The refresh rate must be greater than zero.");
            }

            _maxRefreshRate = value;
        }
    }

    /// <summary>
    /// Default for whether new <see cref="RamblaState"/> instances collect
    /// lifetime <see cref="StateMetrics"/>. Off by default so the hot path stays
    /// allocation- and contention-free; the demo and diagnostics turn it on.
    /// </summary>
    public bool CollectMetrics { get; set; }
}
