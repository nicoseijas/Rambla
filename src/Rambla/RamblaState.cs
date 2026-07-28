using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading;
using Rambla.Scheduling;

namespace Rambla;

/// <summary>
/// Base class for view models whose state is written from background threads at
/// high frequency. Rambla separates <em>state mutation</em> from <em>UI
/// notification</em>: writes mark properties dirty from any thread, and a single
/// coalesced flush is scheduled onto the UI context, where <see
/// cref="INotifyPropertyChanged.PropertyChanged"/> is raised once per property.
/// </summary>
/// <remarks>
/// <para>
/// A batch (<see cref="BeginUpdate"/>) provides <b>notification coherence</b>:
/// observers are never notified mid-batch. It does <b>not</b> provide
/// cross-thread <b>state atomicity</b> — a reader on another thread can still see
/// a new value beside a stale one before the batch closes. Use immutable
/// snapshots when you need snapshot consistency.
/// </para>
/// <para>Threading: all members are safe to call from any thread.</para>
/// <para>
/// Frozen V1 contracts (see SEMANTICS.md): notification order within a flush is
/// unspecified; a subscriber that throws fails fast but never wedges the engine;
/// <c>SetField</c> uses <see cref="EqualityComparer{T}.Default"/> and only a real
/// change counts as a mutation; the type owns no resources and is not
/// <see cref="IDisposable"/> — scheduler lifetime is the caller's.
/// </para>
/// </remarks>
public abstract class RamblaState : INotifyPropertyChanged
{
    private readonly IStateScheduler _scheduler;
    private readonly bool _collectMetrics;
    private readonly object _gate = new();
    private readonly HashSet<string> _dirty = new(StringComparer.Ordinal);

    private int _batchDepth;
    private bool _flushScheduled;

    private long _mutations;
    private long _flushes;
    private long _notifications;

    // Copy-on-write array of attached diagnostics probes; null when none (the
    // common case), so the hot path pays only one volatile read + null check.
    private IStateProbe[]? _probes;

    /// <inheritdoc />
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <param name="scheduler">
    /// The scheduler that marshals flushes to the UI. Defaults to
    /// <see cref="RamblaOptions.Default"/>'s scheduler.
    /// </param>
    /// <param name="collectMetrics">
    /// Whether to collect lifetime <see cref="StateMetrics"/>. Defaults to
    /// <see cref="RamblaOptions.CollectMetrics"/>.
    /// </param>
    protected RamblaState(IStateScheduler? scheduler = null, bool? collectMetrics = null)
    {
        _scheduler = scheduler ?? RamblaOptions.Default.Scheduler;
        _collectMetrics = collectMetrics ?? RamblaOptions.Default.CollectMetrics;
    }

    /// <summary>
    /// A snapshot of lifetime counters. Meaningful only when metrics collection
    /// is enabled; otherwise every counter reads zero.
    /// </summary>
    public StateMetrics Metrics => new(
        Interlocked.Read(ref _mutations),
        Interlocked.Read(ref _flushes),
        Interlocked.Read(ref _notifications));

    /// <summary>
    /// The scheduler this state posts its flushes to. Exposed so state that lives
    /// beside the properties — an <see cref="AsyncStateCommand"/>, for instance —
    /// reaches the UI through the same context.
    /// </summary>
    protected IStateScheduler Scheduler => _scheduler;

    /// <summary>
    /// Marks a property dirty without writing a backing field, so a computed
    /// property (one whose value lives elsewhere) notifies through the same
    /// coalesced flush as any other. Safe to call from any thread.
    /// </summary>
    /// <remarks>
    /// The engine cannot tell whether the underlying value really changed, so
    /// every call counts as a mutation and notifies on the next flush — unlike
    /// <see cref="SetField{T}"/>, which drops no-op writes (SEMANTICS.md §3).
    /// Group several with <see cref="BeginUpdate"/> to keep them in one pass.
    /// </remarks>
    protected void MarkDirty(string propertyName)
    {
        if (propertyName is null)
        {
            throw new ArgumentNullException(nameof(propertyName));
        }

        bool schedule;
        lock (_gate)
        {
            _dirty.Add(propertyName);
            schedule = TryArmFlushNoLock();
        }

        if (_collectMetrics)
        {
            Interlocked.Increment(ref _mutations);
        }

        NotifyProbesOfMutation(propertyName);

        if (schedule)
        {
            ScheduleFlush();
        }
    }

    /// <summary>
    /// Returns <paramref name="command"/>, creating it once via
    /// <paramref name="factory"/> if it is still null. Used by the
    /// <c>[StateCommand]</c> generator: a command needs <see cref="Scheduler"/>,
    /// which a field initializer cannot see (those run before the base
    /// constructor), so commands are built on first access instead.
    /// </summary>
    protected AsyncStateCommand EnsureCommand(ref AsyncStateCommand? command, Func<AsyncStateCommand> factory)
    {
        if (factory is null)
        {
            throw new ArgumentNullException(nameof(factory));
        }

        // Reuses the state's lock rather than paying for a second one in every
        // instance. Monitor is reentrant, so a factory that touches this state
        // cannot deadlock itself.
        lock (_gate)
        {
            return command ??= factory();
        }
    }

    /// <summary>
    /// Assigns <paramref name="value"/> to <paramref name="field"/> if it changed,
    /// marks the property dirty, and (unless inside a batch) schedules a flush.
    /// Safe to call from any thread.
    /// </summary>
    /// <returns><see langword="true"/> if the value changed; otherwise <see langword="false"/>.</returns>
    protected bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (propertyName is null)
        {
            throw new ArgumentNullException(nameof(propertyName));
        }

        bool schedule;
        lock (_gate)
        {
            if (EqualityComparer<T>.Default.Equals(field, value))
            {
                return false;
            }

            field = value;
            _dirty.Add(propertyName);
            schedule = TryArmFlushNoLock();
        }

        if (_collectMetrics)
        {
            Interlocked.Increment(ref _mutations);
        }

        NotifyProbesOfMutation(propertyName);

        // Post outside the lock: an ImmediateStateScheduler runs the flush
        // synchronously, and raising notifications must never happen while _gate
        // is held (reentrancy / deadlock risk if a handler touches the state).
        if (schedule)
        {
            ScheduleFlush();
        }

        return true;
    }

    private void NotifyProbesOfMutation(string propertyName)
    {
        IStateProbe[]? probes = Volatile.Read(ref _probes);
        if (probes is null)
        {
            return;
        }

        for (int i = 0; i < probes.Length; i++)
        {
            // A probe is a pure observer and is documented to never throw. If one
            // does anyway, isolate it: a misbehaving diagnostics probe must never
            // prevent the flush from being scheduled (which would silently and
            // permanently wedge notifications for this instance).
            try
            {
                probes[i].OnMutation(propertyName);
            }
            catch
            {
                // Swallowed by contract: observers cannot affect engine behaviour.
            }
        }
    }

    /// <summary>
    /// Posts the flush, disarming <see cref="_flushScheduled"/> if the scheduler
    /// rejects the post. Without the rollback, every later write would assume a
    /// flush is already pending and the pipeline would silently wedge forever.
    /// </summary>
    private void ScheduleFlush()
    {
        try
        {
            _scheduler.Post(Flush);
        }
        catch
        {
            lock (_gate)
            {
                _flushScheduled = false;
            }

            throw;
        }
    }

    /// <summary>
    /// Attaches a diagnostics <see cref="IStateProbe"/> that observes this state's
    /// mutations and flushes. Returns a token; dispose it to detach. Attaching a
    /// probe does not change any behaviour — it is a pure observer (see
    /// <see cref="IStateProbe"/>). Multiple probes may be attached.
    /// </summary>
    public IDisposable AttachProbe(IStateProbe probe)
    {
        if (probe is null)
        {
            throw new ArgumentNullException(nameof(probe));
        }

        while (true)
        {
            IStateProbe[]? current = Volatile.Read(ref _probes);
            IStateProbe[] updated;
            if (current is null)
            {
                updated = new[] { probe };
            }
            else
            {
                updated = new IStateProbe[current.Length + 1];
                Array.Copy(current, updated, current.Length);
                updated[current.Length] = probe;
            }

            if (Interlocked.CompareExchange(ref _probes, updated, current) == current)
            {
                return new ProbeToken(this, probe);
            }
        }
    }

    private void DetachProbe(IStateProbe probe)
    {
        while (true)
        {
            IStateProbe[]? current = Volatile.Read(ref _probes);
            if (current is null)
            {
                return;
            }

            int index = Array.IndexOf(current, probe);
            if (index < 0)
            {
                return;
            }

            IStateProbe[]? updated;
            if (current.Length == 1)
            {
                updated = null;
            }
            else
            {
                updated = new IStateProbe[current.Length - 1];
                Array.Copy(current, 0, updated, 0, index);
                Array.Copy(current, index + 1, updated, index, current.Length - index - 1);
            }

            if (Interlocked.CompareExchange(ref _probes, updated, current) == current)
            {
                return;
            }
        }
    }

    /// <summary>
    /// Opens a batch: property writes are accumulated without scheduling a flush
    /// until the returned scope is disposed. The batch is coherent to the UI — the
    /// flush raises all accumulated notifications in one pass, so observers are
    /// never notified mid-batch. This is notification coherence, not cross-thread
    /// state atomicity. Batches nest; scopes may be disposed in any order.
    /// </summary>
    public IDisposable BeginUpdate()
    {
        lock (_gate)
        {
            _batchDepth++;
        }

        return new UpdateScope(this);
    }

    /// <summary>Runs <paramref name="mutate"/> inside a single <see cref="BeginUpdate"/> batch.</summary>
    public void Update(Action mutate)
    {
        if (mutate is null)
        {
            throw new ArgumentNullException(nameof(mutate));
        }

        using (BeginUpdate())
        {
            mutate();
        }
    }

    private void EndUpdate()
    {
        bool schedule;
        lock (_gate)
        {
            schedule = --_batchDepth == 0 && _dirty.Count > 0 && TryArmFlushNoLock();
        }

        if (schedule)
        {
            ScheduleFlush();
        }
    }

    /// <summary>
    /// Marks a flush as pending if one is not already armed. Must be called under
    /// <see cref="_gate"/>. Returns whether the caller now owns posting the flush.
    /// </summary>
    private bool TryArmFlushNoLock()
    {
        if (_flushScheduled || _batchDepth > 0)
        {
            return false;
        }

        _flushScheduled = true;
        return true;
    }

    private void Flush()
    {
        string[] names;

        lock (_gate)
        {
            // Clearing the dirty set and lowering the flag happen together under
            // the lock, so a write that lands after this point re-arms a fresh
            // flush and cannot be lost.
            _flushScheduled = false;

            // A flush armed before a batch opened may be dispatched while that
            // batch is still open. Delivering it would notify the batch's own
            // writes mid-batch, so it defers entirely: EndUpdate re-arms on close.
            if (_batchDepth > 0 || _dirty.Count == 0)
            {
                return;
            }

            names = new string[_dirty.Count];
            _dirty.CopyTo(names);
            _dirty.Clear();
        }

        if (_collectMetrics)
        {
            Interlocked.Increment(ref _flushes);
        }

        PropertyChangedEventHandler? handler = PropertyChanged;
        IStateProbe[]? probes = Volatile.Read(ref _probes);

        // Nothing to do at all: no observers of either kind.
        if (handler is null && probes is null)
        {
            return;
        }

        long startTimestamp = probes is null ? 0L : Stopwatch.GetTimestamp();

        if (handler is not null)
        {
            foreach (string name in names)
            {
                handler(this, new PropertyChangedEventArgs(name));
            }

            if (_collectMetrics)
            {
                Interlocked.Add(ref _notifications, names.Length);
            }
        }

        // The probe observes every coalesced flush, whether or not a UI handler is
        // attached — coalescing is a property of the engine, not of the subscriber.
        // The notified flag tells it whether PropertyChanged was actually raised,
        // so diagnostics can distinguish flushed properties from UI events.
        if (probes is not null)
        {
            TimeSpan raiseDuration = handler is null ? TimeSpan.Zero : ElapsedSince(startTimestamp);
            for (int i = 0; i < probes.Length; i++)
            {
                // Isolate each probe: one throwing observer must not stop the others,
                // nor propagate out of the flush onto the scheduler/UI thread.
                try
                {
                    probes[i].OnFlush(names, raiseDuration, handler is not null);
                }
                catch
                {
                    // Swallowed by contract: observers cannot affect engine behaviour.
                }
            }
        }
    }

    private static TimeSpan ElapsedSince(long startTimestamp)
    {
        // Stopwatch.GetElapsedTime is unavailable on netstandard2.0; derive it.
        double seconds = (Stopwatch.GetTimestamp() - startTimestamp) / (double)Stopwatch.Frequency;
        return TimeSpan.FromSeconds(seconds);
    }

    private sealed class UpdateScope : IDisposable
    {
        private RamblaState? _owner;

        public UpdateScope(RamblaState owner) => _owner = owner;

        public void Dispose()
        {
            RamblaState? owner = Interlocked.Exchange(ref _owner, null);
            owner?.EndUpdate();
        }
    }

    private sealed class ProbeToken : IDisposable
    {
        private readonly IStateProbe _probe;
        private RamblaState? _owner;

        public ProbeToken(RamblaState owner, IStateProbe probe)
        {
            _owner = owner;
            _probe = probe;
        }

        public void Dispose()
        {
            RamblaState? owner = Interlocked.Exchange(ref _owner, null);
            owner?.DetachProbe(_probe);
        }
    }
}
