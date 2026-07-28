using System.Windows.Input;
using FluentAssertions;
using Rambla.Scheduling;
using Xunit;

namespace Rambla.Tests;

/// <summary>
/// The run lifecycle of <see cref="AsyncStateCommand"/>: who may start, what
/// happens to a failure, what cancellation means, and how the state reaches the
/// UI. The scheduler is explicit in every test so nothing depends on ambient
/// configuration.
/// </summary>
public sealed class AsyncStateCommandTests
{
    [Fact]
    public async Task Reports_running_while_the_work_is_in_flight()
    {
        TaskCompletionSource<object?> gate = new();
        AsyncStateCommand command = new(_ => gate.Task, scheduler: ImmediateStateScheduler.Instance);

        command.IsRunning.Should().BeFalse();
        command.CanExecute(null).Should().BeTrue();

        Task run = command.ExecuteAsync();

        command.IsRunning.Should().BeTrue();
        command.CanExecute(null).Should().BeFalse("the default policy refuses a second run");

        gate.SetResult(null);
        await run;

        command.IsRunning.Should().BeFalse();
        command.Error.Should().BeNull();
        command.CanExecute(null).Should().BeTrue();
    }

    [Fact]
    public async Task Refuses_a_second_run_while_one_is_in_flight()
    {
        TaskCompletionSource<object?> gate = new();
        int starts = 0;
        AsyncStateCommand command = new(
            _ => { starts++; return gate.Task; },
            scheduler: ImmediateStateScheduler.Instance);

        Task first = command.ExecuteAsync();
        await command.ExecuteAsync();

        starts.Should().Be(1);

        gate.SetResult(null);
        await first;
    }

    [Fact]
    public async Task CancelPrevious_cancels_the_run_in_flight_and_starts_a_new_one()
    {
        TaskCompletionSource<object?> first = new();
        TaskCompletionSource<object?> second = new();
        List<CancellationToken> tokens = new();
        int starts = 0;

        AsyncStateCommand command = new(
            token =>
            {
                tokens.Add(token);
                return starts++ == 0 ? first.Task : second.Task;
            },
            cancelPrevious: true,
            scheduler: ImmediateStateScheduler.Instance);

        Task firstRun = command.ExecuteAsync();
        command.CanExecute(null).Should().BeTrue("latest-wins keeps the command enabled");

        Task secondRun = command.ExecuteAsync();

        tokens[0].IsCancellationRequested.Should().BeTrue("the superseded run is cancelled");
        tokens[1].IsCancellationRequested.Should().BeFalse();

        // The superseded run finishing must not clear the state of the live one.
        first.SetCanceled();
        await firstRun;
        command.IsRunning.Should().BeTrue();

        second.SetResult(null);
        await secondRun;
        command.IsRunning.Should().BeFalse();
    }

    [Fact]
    public async Task A_failure_lands_in_Error_instead_of_being_thrown()
    {
        AsyncStateCommand command = new(
            _ => Task.FromException(new InvalidOperationException("boom")),
            scheduler: ImmediateStateScheduler.Instance);

        await command.ExecuteAsync();

        command.Error.Should().BeOfType<InvalidOperationException>().Which.Message.Should().Be("boom");
        command.IsRunning.Should().BeFalse();
        command.CanExecute(null).Should().BeTrue("a failed run leaves the command usable");
    }

    [Fact]
    public async Task Error_is_cleared_when_the_next_run_starts()
    {
        bool fail = true;
        AsyncStateCommand command = new(
            _ => fail ? Task.FromException(new InvalidOperationException("boom")) : Task.CompletedTask,
            scheduler: ImmediateStateScheduler.Instance);

        await command.ExecuteAsync();
        command.Error.Should().NotBeNull();

        fail = false;
        await command.ExecuteAsync();

        command.Error.Should().BeNull();
    }

    [Fact]
    public async Task Cancellation_is_not_an_error()
    {
        TaskCompletionSource<object?> started = new();
        AsyncStateCommand command = new(
            async token =>
            {
                started.SetResult(null);
                await Task.Delay(Timeout.Infinite, token).ConfigureAwait(false);
            },
            scheduler: ImmediateStateScheduler.Instance);

        Task run = command.ExecuteAsync();
        await started.Task;

        command.Cancel();
        await run;

        command.Error.Should().BeNull();
        command.IsRunning.Should().BeFalse();
    }

    [Fact]
    public async Task The_cancel_command_is_enabled_only_while_running()
    {
        TaskCompletionSource<object?> gate = new();
        AsyncStateCommand command = new(_ => gate.Task, scheduler: ImmediateStateScheduler.Instance);
        ICommand cancel = command.CancelCommand;

        cancel.CanExecute(null).Should().BeFalse();

        Task run = command.ExecuteAsync();
        cancel.CanExecute(null).Should().BeTrue();

        gate.SetResult(null);
        await run;

        cancel.CanExecute(null).Should().BeFalse();
    }

    [Fact]
    public async Task The_cancel_command_cancels_the_token()
    {
        TaskCompletionSource<object?> started = new();
        CancellationToken captured = default;
        AsyncStateCommand command = new(
            async token =>
            {
                captured = token;
                started.SetResult(null);
                await Task.Delay(Timeout.Infinite, token).ConfigureAwait(false);
            },
            scheduler: ImmediateStateScheduler.Instance);

        Task run = command.ExecuteAsync();
        await started.Task;

        command.CancelCommand.Execute(null);
        await run;

        captured.IsCancellationRequested.Should().BeTrue();
    }

    [Fact]
    public async Task The_canExecute_gate_blocks_the_run()
    {
        bool allowed = false;
        int starts = 0;
        AsyncStateCommand command = new(
            _ => { starts++; return Task.CompletedTask; },
            canExecute: () => allowed,
            scheduler: ImmediateStateScheduler.Instance);

        command.CanExecute(null).Should().BeFalse();
        await command.ExecuteAsync();
        starts.Should().Be(0);

        allowed = true;
        command.CanExecute(null).Should().BeTrue();
        await command.ExecuteAsync();
        starts.Should().Be(1);
    }

    [Fact]
    public async Task CanExecuteChanged_is_raised_through_the_scheduler_and_coalesced()
    {
        ManualStateScheduler scheduler = new();
        AsyncStateCommand command = new(_ => Task.CompletedTask, scheduler: scheduler);
        int raised = 0;
        command.CanExecuteChanged += (_, _) => raised++;

        await command.ExecuteAsync();

        raised.Should().Be(0, "the notification waits for the UI context");

        scheduler.Drain();

        raised.Should().Be(1, "start and finish inside one window coalesce into a single raise");
    }

    [Fact]
    public async Task StateChanged_reports_every_transition_on_the_spot()
    {
        TaskCompletionSource<object?> gate = new();
        AsyncStateCommand command = new(_ => gate.Task, scheduler: ImmediateStateScheduler.Instance);
        List<bool> observed = new();
        command.StateChanged += (_, _) => observed.Add(command.IsRunning);

        Task run = command.ExecuteAsync();
        gate.SetResult(null);
        await run;

        observed.Should().Equal(new[] { true, false });
    }

    [Fact]
    public async Task A_superseded_failure_does_not_overwrite_the_live_run()
    {
        TaskCompletionSource<object?> first = new();
        TaskCompletionSource<object?> second = new();
        int starts = 0;

        AsyncStateCommand command = new(
            _ => starts++ == 0 ? first.Task : second.Task,
            cancelPrevious: true,
            scheduler: ImmediateStateScheduler.Instance);

        Task firstRun = command.ExecuteAsync();
        Task secondRun = command.ExecuteAsync();

        first.SetException(new InvalidOperationException("stale"));
        await firstRun;

        command.Error.Should().BeNull("the failure belongs to a run that was already replaced");

        second.SetResult(null);
        await secondRun;
        command.Error.Should().BeNull();
    }

    [Fact]
    public void Execute_forgets_the_task_but_still_captures_the_failure()
    {
        AsyncStateCommand command = new(
            _ => Task.FromException(new InvalidOperationException("boom")),
            scheduler: ImmediateStateScheduler.Instance);

        // ICommand.Execute is void: the failure must not escape as an unobserved
        // task exception, it must land in Error.
        command.Execute(null);

        command.Error.Should().BeOfType<InvalidOperationException>();
    }

    [Fact]
    public void State_changes_come_back_to_the_context_the_command_was_invoked_on()
    {
        // Regression: the run used to resume on a thread pool thread, so with an
        // inline scheduler CanExecuteChanged was raised off the UI thread. WPF
        // throws there, the exception vanished into the fire-and-forget Execute,
        // and the bound button stayed disabled forever.
        PumpingSynchronizationContext context = new();
        SynchronizationContext? previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(context);

        try
        {
            TaskCompletionSource<object?> gate = new();
            AsyncStateCommand command = new(_ => gate.Task, scheduler: ImmediateStateScheduler.Instance);
            int invokedOn = Environment.CurrentManagedThreadId;
            List<int> raisedOn = new();
            command.CanExecuteChanged += (_, _) => raisedOn.Add(Environment.CurrentManagedThreadId);

            Task run = command.ExecuteAsync();

            // Complete the work from somewhere else entirely, as a real background
            // operation does.
            Task.Run(() => gate.SetResult(null));

            context.PumpUntil(run, TimeSpan.FromSeconds(10));

            run.IsCompleted.Should().BeTrue();
            command.IsRunning.Should().BeFalse();
            raisedOn.Should().OnlyContain(id => id == invokedOn,
                "every notification must reach the thread the command was invoked on");
            command.CanExecute(null).Should().BeTrue();
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previous);
        }
    }

    /// <summary>
    /// A minimal stand-in for a UI thread: it queues posted callbacks and only
    /// runs them when the owning thread pumps, exactly as a dispatcher does.
    /// </summary>
    private sealed class PumpingSynchronizationContext : SynchronizationContext
    {
        private readonly System.Collections.Concurrent.ConcurrentQueue<(SendOrPostCallback Callback, object? State)> _queue = new();

        public override void Post(SendOrPostCallback d, object? state) => _queue.Enqueue((d, state));

        public override void Send(SendOrPostCallback d, object? state) => d(state);

        /// <summary>Runs queued callbacks until <paramref name="until"/> completes.</summary>
        public void PumpUntil(Task until, TimeSpan timeout)
        {
            System.Diagnostics.Stopwatch clock = System.Diagnostics.Stopwatch.StartNew();
            while (!until.IsCompleted && clock.Elapsed < timeout)
            {
                if (_queue.TryDequeue(out (SendOrPostCallback Callback, object? State) work))
                {
                    work.Callback(work.State);
                }
                else
                {
                    Thread.Sleep(1);
                }
            }

            while (_queue.TryDequeue(out (SendOrPostCallback Callback, object? State) rest))
            {
                rest.Callback(rest.State);
            }
        }
    }

    // --- integration with a state, the shape [StateCommand] generates ---

    [Fact]
    public async Task Busy_and_error_projections_notify_through_the_state_flush()
    {
        ManualStateScheduler scheduler = new();
        SearchViewModel vm = new(scheduler);
        List<string?> notified = new();
        vm.PropertyChanged += (_, e) => notified.Add(e.PropertyName);

        await vm.SearchCommand.ExecuteAsync();
        scheduler.Drain();

        // Two transitions (start, finish), each marking both projections, all
        // coalesced into one flush that notifies each property once.
        notified.Should().BeEquivalentTo(new[] { nameof(SearchViewModel.IsSearching), nameof(SearchViewModel.SearchError) });
        vm.IsSearching.Should().BeFalse();
        vm.SearchError.Should().BeOfType<InvalidOperationException>();
    }

    /// <summary>
    /// Hand-written mirror of what <c>[StateCommand]</c> emits, so the runtime
    /// contract is tested without running the generator (the generator's own
    /// output is verified in <see cref="CommandGeneratorTests"/>).
    /// </summary>
    private sealed class SearchViewModel : RamblaState
    {
        private readonly Exception _failure = new InvalidOperationException("no results");

        private AsyncStateCommand? _searchCommand;

        public SearchViewModel(IStateScheduler scheduler)
            : base(scheduler)
        {
        }

        public AsyncStateCommand SearchCommand => EnsureCommand(ref _searchCommand, CreateSearchCommand);

        public bool IsSearching => SearchCommand.IsRunning;

        public Exception? SearchError => SearchCommand.Error;

        private Task SearchAsync(CancellationToken token) => Task.FromException(_failure);

        private AsyncStateCommand CreateSearchCommand()
        {
            var command = new AsyncStateCommand(
                token => SearchAsync(token),
                canExecute: null,
                cancelPrevious: true,
                scheduler: Scheduler);

            command.StateChanged += (_, _) =>
            {
                using (BeginUpdate())
                {
                    MarkDirty(nameof(IsSearching));
                    MarkDirty(nameof(SearchError));
                }
            };

            return command;
        }
    }
}
