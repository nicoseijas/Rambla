using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using Rambla.Scheduling;

namespace Rambla;

/// <summary>
/// An asynchronous <see cref="ICommand"/> that owns its own run lifecycle:
/// whether it is running, the error the last run failed with, and the token that
/// cancels it. Bind to it directly, or let <c>[StateCommand]</c> generate it
/// together with the matching state properties on your <see cref="RamblaState"/>.
/// </summary>
/// <remarks>
/// <para>
/// Concurrency is a policy, not an accident. By default the command refuses to
/// start while a run is in flight (<see cref="ICommand.CanExecute"/> is
/// <see langword="false"/>, so the button disables itself). With
/// <c>cancelPrevious</c> it is latest-wins instead: a new invocation cancels the
/// one in flight and replaces it — the as-you-type search case.
/// </para>
/// <para>
/// Exceptions are captured into <see cref="Error"/>, never rethrown at the
/// caller: an <see cref="ICommand"/> is invoked from a UI gesture with nobody to
/// catch it, and an unobserved <see cref="Task"/> exception would surface far
/// from the cause. Cancellation is not an error — it leaves
/// <see cref="Error"/> null.
/// </para>
/// <para>
/// Threading: every member is safe to call from any thread.
/// <see cref="CanExecuteChanged"/> is raised through the scheduler, so a run that
/// completes on a worker thread still notifies WPF on the UI thread.
/// </para>
/// </remarks>
public sealed class AsyncStateCommand : ICommand
{
    private readonly Func<CancellationToken, Task> _execute;
    private readonly Func<bool>? _canExecute;
    private readonly IStateScheduler _scheduler;
    private readonly bool _cancelPrevious;
    private readonly object _gate = new();
    private readonly CancelStateCommand _cancelCommand;

    private CancellationTokenSource? _running;
    private volatile bool _isRunning;
    private Exception? _error;
    private int _notifyScheduled;

    /// <param name="execute">The work to run. The token is cancelled by <see cref="Cancel"/>, and by a newer run when <paramref name="cancelPrevious"/> is set.</param>
    /// <param name="canExecute">An extra gate evaluated on top of the run policy. Call <see cref="NotifyCanExecuteChanged"/> when what it reads changes.</param>
    /// <param name="cancelPrevious">
    /// <see langword="true"/> for latest-wins: invoking while a run is in flight
    /// cancels that run and starts a new one. <see langword="false"/> (the
    /// default) refuses to start a second run.
    /// </param>
    /// <param name="scheduler">
    /// Marshals <see cref="CanExecuteChanged"/> to the UI context. Defaults to
    /// <see cref="RamblaOptions.Default"/>'s scheduler; generated commands receive
    /// the owning state's scheduler.
    /// </param>
    public AsyncStateCommand(
        Func<CancellationToken, Task> execute,
        Func<bool>? canExecute = null,
        bool cancelPrevious = false,
        IStateScheduler? scheduler = null)
    {
        _execute = execute ?? throw new ArgumentNullException(nameof(execute));
        _canExecute = canExecute;
        _cancelPrevious = cancelPrevious;
        _scheduler = scheduler ?? RamblaOptions.Default.Scheduler;
        _cancelCommand = new CancelStateCommand(this);
    }

    /// <summary>Raised when <see cref="CanExecute"/> may have changed, on the scheduler's context.</summary>
    public event EventHandler? CanExecuteChanged;

    /// <summary>
    /// Raised when the run state changes (started, failed, finished, cancelled),
    /// on the thread the transition happened on. Generated commands use it to mark
    /// the owning state's busy/error properties dirty.
    /// </summary>
    public event EventHandler? StateChanged;

    /// <summary>Whether a run is currently in flight.</summary>
    public bool IsRunning => _isRunning;

    /// <summary>
    /// The exception the last run failed with, or <see langword="null"/> if it
    /// succeeded, was cancelled, or has not run yet. Cleared when a run starts.
    /// </summary>
    public Exception? Error => Volatile.Read(ref _error);

    /// <summary>Whether there is a run to cancel — the <see cref="CancelCommand"/>'s gate.</summary>
    public bool CanBeCanceled => IsRunning;

    /// <summary>Cancels the run in flight. Enabled only while one is running.</summary>
    public ICommand CancelCommand => _cancelCommand;

    /// <inheritdoc />
    public bool CanExecute(object? parameter)
        => (_cancelPrevious || !IsRunning) && (_canExecute is null || _canExecute());

    /// <summary>
    /// Starts a run and forgets it: failures land in <see cref="Error"/>. Await
    /// <see cref="ExecuteAsync"/> instead when you need to know it finished.
    /// </summary>
    public void Execute(object? parameter) => _ = ExecuteAsync();

    /// <summary>
    /// Starts a run and returns a task that completes when it does — including
    /// when it fails or is cancelled, which are reported through
    /// <see cref="Error"/> and <see cref="IsRunning"/> rather than thrown. Returns
    /// a completed task if the command cannot execute right now.
    /// </summary>
    public Task ExecuteAsync()
    {
        // Evaluated outside the lock: the caller's gate is arbitrary code and must
        // not run while the command's own state is locked.
        if (!CanExecute(null))
        {
            return Task.CompletedTask;
        }

        CancellationTokenSource source;

        lock (_gate)
        {
            if (_running is not null)
            {
                // Re-checked under the lock: two threads can both pass the gate above.
                if (!_cancelPrevious)
                {
                    return Task.CompletedTask;
                }

                // Latest wins: the superseded run is cancelled but still owns its
                // own teardown — it disposes its source and, seeing it is no
                // longer current, publishes neither its error nor IsRunning=false.
                CancelSafely(_running);
            }

            source = new CancellationTokenSource();
            _running = source;
        }

        Volatile.Write(ref _error, null);
        _isRunning = true;
        OnStateChanged();

        return RunAsync(source);
    }

    /// <summary>Cancels the run in flight, if any. A no-op when nothing is running.</summary>
    public void Cancel()
    {
        lock (_gate)
        {
            if (_running is not null)
            {
                CancelSafely(_running);
            }
        }
    }

    /// <summary>
    /// Re-evaluates <see cref="CanExecute"/>. Call it when the state your
    /// <c>canExecute</c> delegate reads has changed.
    /// </summary>
    public void NotifyCanExecuteChanged() => ScheduleNotification();

    private async Task RunAsync(CancellationTokenSource source)
    {
        try
        {
            // Deliberately NOT ConfigureAwait(false), the usual library default:
            // the teardown below publishes UI-facing state (IsRunning, Error,
            // CanExecuteChanged). Suppressing the context resumes it on a thread
            // pool thread, and a scheduler that runs flushes inline — the
            // ImmediateStateScheduler a UI-thread-owned state legitimately uses —
            // would then raise CanExecuteChanged off the UI thread, where WPF
            // throws and the exception is lost in a fire-and-forget Execute.
            // Resuming on the context the command was invoked from keeps the run's
            // observable state on that thread whatever the scheduler does.
            await _execute(source.Token);
        }
        catch (OperationCanceledException) when (source.IsCancellationRequested)
        {
            // Cancellation is a normal outcome, not a failure.
        }
        catch (Exception ex)
        {
            // A superseded run must not overwrite the current one's state.
            if (IsCurrent(source))
            {
                Volatile.Write(ref _error, ex);
            }
        }
        finally
        {
            bool current;
            lock (_gate)
            {
                current = ReferenceEquals(_running, source);
                if (current)
                {
                    _running = null;
                }
            }

            source.Dispose();

            if (current)
            {
                _isRunning = false;
            }

            OnStateChanged();
        }
    }

    private bool IsCurrent(CancellationTokenSource source)
    {
        lock (_gate)
        {
            return ReferenceEquals(_running, source);
        }
    }

    private static void CancelSafely(CancellationTokenSource source)
    {
        try
        {
            source.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // The run finished and disposed its source between the two lines.
        }
    }

    private void OnStateChanged()
    {
        InvokeEventHandlers(StateChanged);
        ScheduleNotification();
    }

    /// <summary>
    /// Posts one <see cref="CanExecuteChanged"/> raise onto the scheduler.
    /// Coalesced the same way a state flush is: several transitions inside one
    /// window produce a single notification, and a rejected post rolls the flag
    /// back instead of wedging every later one.
    /// </summary>
    private void ScheduleNotification()
    {
        if (Interlocked.CompareExchange(ref _notifyScheduled, 1, 0) != 0)
        {
            return;
        }

        try
        {
            _scheduler.Post(RaiseCanExecuteChanged);
        }
        catch
        {
            Volatile.Write(ref _notifyScheduled, 0);
            throw;
        }
    }

    private void RaiseCanExecuteChanged()
    {
        Volatile.Write(ref _notifyScheduled, 0);
        InvokeEventHandlers(CanExecuteChanged);
        _cancelCommand.RaiseCanExecuteChanged();
    }

    /// <summary>
    /// Invokes a command lifecycle event without allowing one observer to change
    /// command execution. Unlike <see cref="RamblaState.PropertyChanged"/>, these
    /// are lifecycle observers: a throw during the start transition used to abort
    /// the method before <see cref="RunAsync"/> was even called, leaving
    /// <see cref="IsRunning"/> permanently true.
    /// </summary>
    private void InvokeEventHandlers(EventHandler? handlers)
    {
        if (handlers is null)
        {
            return;
        }

        foreach (EventHandler handler in handlers.GetInvocationList())
        {
            try
            {
                handler(this, EventArgs.Empty);
            }
            catch
            {
                // Observers must not prevent the command from reaching its next
                // state or stop later observers (including generated projections).
            }
        }
    }

    /// <summary>
    /// The companion cancel command. It is a plain gate over the owner rather
    /// than an <see cref="AsyncStateCommand"/> of its own: cancelling is
    /// synchronous and must stay available while the owner is busy.
    /// </summary>
    private sealed class CancelStateCommand : ICommand
    {
        private readonly AsyncStateCommand _owner;

        public CancelStateCommand(AsyncStateCommand owner) => _owner = owner;

        public event EventHandler? CanExecuteChanged;

        public bool CanExecute(object? parameter) => _owner.CanBeCanceled;

        public void Execute(object? parameter) => _owner.Cancel();

        public void RaiseCanExecuteChanged() => _owner.InvokeEventHandlers(CanExecuteChanged);
    }
}
