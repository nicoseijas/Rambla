using System.ComponentModel;
using FluentAssertions;
using Microsoft.UI.Dispatching;
using Rambla.Scheduling;
using Xunit;

namespace Rambla.WinUI.Tests;

/// <summary>
/// The WinUI adapter uses an internal enqueue seam so its scheduler contract can
/// be tested without starting a WinUI app or a native dispatcher loop.
/// </summary>
public sealed class DispatcherStateSchedulerTests
{
    [Fact]
    public void Posts_flushes_at_low_priority_by_default()
    {
        FakeQueue queue = new();
        DispatcherStateScheduler scheduler = new(queue.TryEnqueue);

        scheduler.Post(() => { });

        queue.Posted.Should().ContainSingle()
            .Which.Priority.Should().Be(DispatcherQueuePriority.Low,
                "high-frequency state must not starve input or rendering");
    }

    [Fact]
    public void Honours_an_explicit_priority()
    {
        FakeQueue queue = new();
        DispatcherStateScheduler scheduler = new(queue.TryEnqueue, DispatcherQueuePriority.High);

        scheduler.Post(() => { });

        queue.Posted.Should().ContainSingle().Which.Priority.Should().Be(DispatcherQueuePriority.High);
    }

    [Fact]
    public void Does_not_run_the_flush_inline()
    {
        FakeQueue queue = new();
        DispatcherStateScheduler scheduler = new(queue.TryEnqueue);
        bool ran = false;

        scheduler.Post(() => ran = true);

        ran.Should().BeFalse("posting is asynchronous — that deferral is what makes a burst coalesce");

        queue.Drain();

        ran.Should().BeTrue();
    }

    [Fact]
    public void Rejects_a_null_dispatcher_queue()
    {
        Action construct = () => _ = new DispatcherStateScheduler((DispatcherQueue)null!).ToString();

        construct.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Rejected_enqueue_throws_so_the_state_can_recover()
    {
        DispatcherStateScheduler scheduler = new((_, _) => false);

        Action post = () => scheduler.Post(() => { });

        post.Should().Throw<InvalidOperationException>()
            .WithMessage("*DispatcherQueue rejected*");
    }

    [Fact]
    public void A_burst_of_writes_collapses_into_one_notification_pass()
    {
        FakeQueue queue = new();
        QuoteViewModel vm = new(new DispatcherStateScheduler(queue.TryEnqueue));
        List<string?> notified = new();
        vm.PropertyChanged += (_, e) => notified.Add(e.PropertyName);

        for (int i = 1; i <= 1_000; i++)
        {
            vm.Bid = i;
            vm.Ask = i + 1;
        }

        notified.Should().BeEmpty("nothing reaches the UI until the dispatcher runs the flush");
        queue.Posted.Should().ContainSingle("the state posts one pending flush at a time");

        queue.Drain();

        notified.Should().BeEquivalentTo(new[] { nameof(QuoteViewModel.Bid), nameof(QuoteViewModel.Ask) });
        vm.Bid.Should().Be(1_000);
    }

    [Fact]
    public void Composes_with_the_throttling_scheduler()
    {
        FakeQueue queue = new();
        using ThrottlingStateScheduler scheduler = new(new DispatcherStateScheduler(queue.TryEnqueue), 60);

        scheduler.Post(() => { });

        queue.Posted.Should().ContainSingle("the first post after an idle interval is released immediately");
    }

    /// <summary>Captures enqueues instead of running them, like a UI thread that has not pumped yet.</summary>
    private sealed class FakeQueue
    {
        public List<(Action Action, DispatcherQueuePriority Priority)> Posted { get; } = new();

        public bool TryEnqueue(DispatcherQueuePriority priority, Action action)
        {
            Posted.Add((action, priority));
            return true;
        }

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
