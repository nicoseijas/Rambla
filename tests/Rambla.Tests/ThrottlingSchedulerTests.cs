using System.Diagnostics;
using FluentAssertions;
using Rambla.Scheduling;
using Xunit;

namespace Rambla.Tests;

/// <summary>
/// The refresh-rate contract of <see cref="ThrottlingStateScheduler"/>: the first
/// post after an idle period is released immediately, everything posted inside
/// the same window waits for it, and one release drains the whole backlog in a
/// single pass. The clock is a <see cref="FakeThrottleTimer"/>, so the tests
/// assert the window itself rather than a sleep.
/// </summary>
public sealed class ThrottlingSchedulerTests
{
    private const int RefreshRate = 60;

    /// <summary>
    /// 60 Hz against the fake timer's millisecond ticks. 1000/60 is 16.67, rounded
    /// up so the rate stays a ceiling — 16 ms would release 62.5 times a second.
    /// </summary>
    private static readonly TimeSpan Interval = TimeSpan.FromMilliseconds(17);

    [Fact]
    public void First_post_after_an_idle_period_is_released_immediately()
    {
        FakeThrottleTimer timer = new();
        using ThrottlingStateScheduler scheduler = Build(timer, out List<string> ran);

        scheduler.Post(() => ran.Add("a"));

        ran.Should().Equal("a");
        timer.Scheduled.Should().BeNull("an immediate release needs no timer");
    }

    [Fact]
    public void Posts_inside_the_same_window_wait_for_the_rest_of_the_interval()
    {
        FakeThrottleTimer timer = new();
        using ThrottlingStateScheduler scheduler = Build(timer, out List<string> ran);

        scheduler.Post(() => ran.Add("a"));
        timer.Advance(TimeSpan.FromMilliseconds(5));
        scheduler.Post(() => ran.Add("b"));
        scheduler.Post(() => ran.Add("c"));

        ran.Should().Equal("a");
        timer.Scheduled.Should().Be(Interval - TimeSpan.FromMilliseconds(5), "the interval minus the time already elapsed");
        timer.ScheduleCount.Should().Be(1, "a burst arms one release, not one per post");

        timer.FireScheduled();

        ran.Should().Equal("a", "b", "c");
    }

    [Fact]
    public void A_burst_of_states_collapses_into_one_notification_pass()
    {
        FakeThrottleTimer timer = new();
        using ThrottlingStateScheduler scheduler = new(ImmediateStateScheduler.Instance, RefreshRate, timer);
        MarketViewModel vm = new(scheduler);
        List<string?> notified = new();
        vm.PropertyChanged += (_, e) => notified.Add(e.PropertyName);

        // The first write opens the window and is released on the spot.
        vm.Bid = 1m;
        notified.Should().Equal(nameof(MarketViewModel.Bid));
        notified.Clear();

        // 10,000 writes inside one window: latest-value-wins, one pass, one
        // notification per property that actually changed.
        for (int i = 1; i <= 10_000; i++)
        {
            vm.Bid = i;
            vm.Ask = i + 1;
        }

        notified.Should().BeEmpty("nothing may notify before the window closes");

        timer.FireScheduled();

        notified.Should().BeEquivalentTo(new[] { nameof(MarketViewModel.Bid), nameof(MarketViewModel.Ask) });
        vm.Bid.Should().Be(10_000m);
        vm.Ask.Should().Be(10_001m);
    }

    [Fact]
    public void A_flush_posted_while_draining_waits_for_the_next_window()
    {
        FakeThrottleTimer timer = new();
        using ThrottlingStateScheduler scheduler = Build(timer, out List<string> ran);

        // Let the first window close so the next post is released on its own.
        scheduler.Post(() => ran.Add("a"));
        timer.Advance(Interval);

        scheduler.Post(() =>
        {
            ran.Add("b");
            scheduler.Post(() => ran.Add("re-posted"));
        });

        ran.Should().Equal(new[] { "a", "b" }, "the re-posted flush must not extend this pass");
        timer.Scheduled.Should().Be(Interval);

        timer.FireScheduled();

        ran.Should().Equal("a", "b", "re-posted");
    }

    [Fact]
    public void A_throwing_flush_propagates_without_stranding_the_ones_queued_behind_it()
    {
        FakeThrottleTimer timer = new();
        using ThrottlingStateScheduler scheduler = Build(timer, out List<string> ran);

        // The first post opens the window; the next two queue behind it, and the
        // first of those throws when the window closes.
        scheduler.Post(() => ran.Add("first"));
        scheduler.Post(() => throw new InvalidOperationException("subscriber blew up"));
        scheduler.Post(() => ran.Add("survivor"));

        Action fire = timer.FireScheduled;
        fire.Should().Throw<InvalidOperationException>().WithMessage("subscriber blew up");

        ran.Should().Equal(new[] { "first" }, "the aborted pass raises nothing after the throw");
        timer.Scheduled.Should().NotBeNull("the stranded flush must be re-armed");

        timer.FireScheduled();

        ran.Should().Equal("first", "survivor");
    }

    [Fact]
    public void A_state_still_flushes_after_a_subscriber_throws_inside_the_window()
    {
        FakeThrottleTimer timer = new();
        using ThrottlingStateScheduler scheduler = new(ImmediateStateScheduler.Instance, RefreshRate, timer);
        MarketViewModel vm = new(scheduler);
        bool throwNext = true;
        List<string?> notified = new();
        vm.PropertyChanged += (_, e) =>
        {
            if (throwNext)
            {
                throwNext = false;
                throw new InvalidOperationException("boom");
            }

            notified.Add(e.PropertyName);
        };

        Action write = () => vm.Bid = 1m;
        write.Should().Throw<InvalidOperationException>().WithMessage("boom");

        timer.Advance(Interval);
        vm.Ask = 2m;

        notified.Should().Equal(
            new[] { nameof(MarketViewModel.Ask) }, "the engine is left schedulable (SEMANTICS §1)");
    }

    [Fact]
    public void Releases_are_capped_at_the_refresh_rate_over_a_sustained_feed()
    {
        FakeThrottleTimer timer = new();
        int releases = 0;
        CountingScheduler inner = new(() => releases++);
        using ThrottlingStateScheduler scheduler = new(inner, RefreshRate, timer);

        // One second of wall clock, a post every millisecond.
        for (int ms = 0; ms < 1_000; ms++)
        {
            scheduler.Post(static () => { });
            timer.Advance(TimeSpan.FromMilliseconds(1));

            if (timer.IsDue)
            {
                timer.FireScheduled();
            }
        }

        releases.Should().BeLessThanOrEqualTo(RefreshRate, "1,000 posts must collapse to at most 60 releases");
        releases.Should().BeGreaterThan(RefreshRate / 2, "and the feed must still reach the UI");
    }

    [Fact]
    public void Interval_and_rate_are_reported_from_the_configured_refresh_rate()
    {
        FakeThrottleTimer timer = new();
        using ThrottlingStateScheduler scheduler = new(ImmediateStateScheduler.Instance, 50, timer);

        scheduler.MaxRefreshRate.Should().Be(50);
        scheduler.Interval.Should().Be(TimeSpan.FromMilliseconds(20));
    }

    [Fact]
    public void The_ambient_MaxRefreshRate_is_the_default_rate()
    {
        int previous = RamblaOptions.Default.MaxRefreshRate;
        try
        {
            RamblaOptions.Default.MaxRefreshRate = 30;
            using ThrottlingStateScheduler scheduler = new(ImmediateStateScheduler.Instance);

            scheduler.MaxRefreshRate.Should().Be(30);
        }
        finally
        {
            RamblaOptions.Default.MaxRefreshRate = previous;
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void A_non_positive_refresh_rate_is_rejected(int rate)
    {
        Action construct = () => new ThrottlingStateScheduler(ImmediateStateScheduler.Instance, rate).Dispose();
        construct.Should().Throw<ArgumentOutOfRangeException>();

        Action assign = () => RamblaOptions.Default.MaxRefreshRate = rate;
        assign.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Posting_after_dispose_throws_instead_of_silently_dropping_the_flush()
    {
        FakeThrottleTimer timer = new();
        ThrottlingStateScheduler scheduler = Build(timer, out List<string> ran);

        scheduler.Dispose();

        Action post = () => scheduler.Post(() => ran.Add("late"));
        post.Should().Throw<ObjectDisposedException>();
        ran.Should().BeEmpty();
        timer.Disposed.Should().BeTrue();
    }

    [Fact]
    public void Concurrent_writers_all_land_within_one_window()
    {
        FakeThrottleTimer timer = new();
        using ThrottlingStateScheduler scheduler = new(ImmediateStateScheduler.Instance, RefreshRate, timer);
        MarketViewModel vm = new(scheduler);
        int notifications = 0;
        vm.PropertyChanged += (_, _) => Interlocked.Increment(ref notifications);

        Parallel.For(0, 8, worker =>
        {
            for (int i = 1; i <= 5_000; i++)
            {
                vm.Bid = (worker * 5_000) + i;
            }
        });

        // Whatever the interleaving, every write is either already delivered or
        // waiting on an armed release — none is stranded.
        while (timer.Scheduled is not null)
        {
            timer.FireScheduled();
        }

        notifications.Should().BeGreaterThan(0);
        vm.Bid.Should().NotBe(0m);
    }

    [Fact]
    public void A_real_timer_bounds_the_flush_rate_under_a_live_feed()
    {
        int flushes = 0;
        CountingScheduler inner = new(() => flushes++);
        using ThrottlingStateScheduler scheduler = new(inner, 50);

        Stopwatch clock = Stopwatch.StartNew();
        while (clock.Elapsed < TimeSpan.FromMilliseconds(400))
        {
            scheduler.Post(static () => { });
        }

        // 400 ms at 50 Hz is ~20 releases. The bound is generous because the OS
        // timer resolution only ever makes the real rate *lower*, never higher.
        flushes.Should().BeLessThan(40);
    }

    private static ThrottlingStateScheduler Build(FakeThrottleTimer timer, out List<string> ran)
    {
        ran = new List<string>();
        return new ThrottlingStateScheduler(ImmediateStateScheduler.Instance, RefreshRate, timer);
    }

    /// <summary>An inner scheduler that counts releases and runs them inline.</summary>
    private sealed class CountingScheduler : IStateScheduler
    {
        private readonly Action _onPost;

        public CountingScheduler(Action onPost) => _onPost = onPost;

        public void Post(Action flush)
        {
            _onPost();
            flush();
        }
    }
}
