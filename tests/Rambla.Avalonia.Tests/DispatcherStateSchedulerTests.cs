using System.ComponentModel;
using Avalonia.Threading;
using FluentAssertions;
using Rambla.Scheduling;
using Xunit;

namespace Rambla.Avalonia.Tests;

/// <summary>
/// The Avalonia adapter. It is deliberately built on <see cref="IDispatcher"/>
/// rather than the concrete <c>Dispatcher</c>, so the marshaling contract can be
/// asserted with a fake instead of a live UI thread.
/// </summary>
public sealed class DispatcherStateSchedulerTests
{
    [Fact]
    public void Posts_flushes_at_background_priority_by_default()
    {
        FakeDispatcher dispatcher = new();
        DispatcherStateScheduler scheduler = new(dispatcher);

        scheduler.Post(() => { });

        dispatcher.Posted.Should().ContainSingle()
            .Which.Priority.Should().Be(DispatcherPriority.Background,
                "high-frequency state must not starve input or rendering");
    }

    [Fact]
    public void Honours_an_explicit_priority()
    {
        FakeDispatcher dispatcher = new();
        DispatcherStateScheduler scheduler = new(dispatcher, DispatcherPriority.Render);

        scheduler.Post(() => { });

        dispatcher.Posted.Should().ContainSingle().Which.Priority.Should().Be(DispatcherPriority.Render);
    }

    [Fact]
    public void Does_not_run_the_flush_inline()
    {
        FakeDispatcher dispatcher = new();
        DispatcherStateScheduler scheduler = new(dispatcher);
        bool ran = false;

        scheduler.Post(() => ran = true);

        ran.Should().BeFalse("posting is asynchronous — that deferral is what makes a burst coalesce");

        dispatcher.Drain();

        ran.Should().BeTrue();
    }

    [Fact]
    public void Rejects_a_null_dispatcher()
    {
        Action construct = () => _ = new DispatcherStateScheduler(null!).ToString();

        construct.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void A_burst_of_writes_collapses_into_one_notification_pass()
    {
        FakeDispatcher dispatcher = new();
        QuoteViewModel vm = new(new DispatcherStateScheduler(dispatcher));
        List<string?> notified = new();
        vm.PropertyChanged += (_, e) => notified.Add(e.PropertyName);

        for (int i = 1; i <= 1_000; i++)
        {
            vm.Bid = i;
            vm.Ask = i + 1;
        }

        notified.Should().BeEmpty("nothing reaches the UI until the dispatcher runs the flush");
        dispatcher.Posted.Should().ContainSingle("the state posts one pending flush at a time");

        dispatcher.Drain();

        notified.Should().BeEquivalentTo(new[] { nameof(QuoteViewModel.Bid), nameof(QuoteViewModel.Ask) });
        vm.Bid.Should().Be(1_000);
    }

    [Fact]
    public void Composes_with_the_throttling_scheduler()
    {
        FakeDispatcher dispatcher = new();
        using ThrottlingStateScheduler scheduler = new(new DispatcherStateScheduler(dispatcher), 60);

        scheduler.Post(() => { });

        dispatcher.Posted.Should().ContainSingle("the first post after an idle interval is released immediately");
    }

    /// <summary>Captures posts instead of running them, like a UI thread that has not pumped yet.</summary>
    private sealed class FakeDispatcher : IDispatcher
    {
        public List<(Action Action, DispatcherPriority Priority)> Posted { get; } = new();

        public bool CheckAccess() => true;

        public void VerifyAccess()
        {
        }

        public void Post(Action action, DispatcherPriority priority = default) => Posted.Add((action, priority));

        /// <summary>Runs every captured post, in order.</summary>
        public void Drain()
        {
            var pending = Posted.ToArray();
            Posted.Clear();
            foreach ((Action action, _) in pending)
            {
                action();
            }
        }
    }

    private sealed class QuoteViewModel : RamblaState
    {
        private decimal _bid;
        private decimal _ask;

        public QuoteViewModel(IStateScheduler scheduler)
            : base(scheduler)
        {
        }

        public decimal Bid
        {
            get => _bid;
            set => SetField(ref _bid, value);
        }

        public decimal Ask
        {
            get => _ask;
            set => SetField(ref _ask, value);
        }
    }
}
