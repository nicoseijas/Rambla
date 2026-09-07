using System.Collections.Specialized;
using FluentAssertions;
using Rambla.Scheduling;
using Rambla.WinUI.Virtualization;
using Xunit;

namespace Rambla.WinUI.Tests;

public sealed class VirtualizingListTests
{
    [Fact]
    public void Exposes_the_full_logical_count_without_materializing_the_source()
    {
        RecordingRangeSource source = new(500_000);
        using VirtualizingList<Row> list = CreateList(source);

        list.Count.Should().Be(500_000);
        list.CachedItemCount.Should().Be(0);

        Row item = list[250_000];

        item.IsPlaceholder.Should().BeTrue();
        list.CachedItemCount.Should().Be(0, "an item outside WinUI's retained range must not grow the cache");
        source.Requests.Should().BeEmpty();
    }

    [Fact]
    public void Loads_the_visible_window_and_keeps_the_cache_bounded()
    {
        RecordingRangeSource source = new(500_000);
        using VirtualizingList<Row> list = CreateList(source);

        list.UpdateRanges(new VirtualizingList<Row>.IndexRange(100_000, 20), []);

        source.Requests.Should().ContainSingle()
            .Which.Should().Be(new RequestedRange(99_800, 420));
        list.Count.Should().Be(500_000, "the scroll bar must represent the complete source");
        list.CachedItemCount.Should().Be(420);
        list[100_000].IsPlaceholder.Should().BeFalse();

        list.UpdateRanges(new VirtualizingList<Row>.IndexRange(300_000, 20), []);

        list.CachedItemCount.Should().Be(420);
        list[100_000].IsPlaceholder.Should().BeTrue("the old viewport was evicted");
        list[300_000].IsPlaceholder.Should().BeFalse();
    }

    [Fact]
    public async Task Replaces_an_observed_loading_item_when_its_range_arrives()
    {
        DeferredRangeSource source = new(500_000);
        using VirtualizingList<Row> list = CreateList(source);
        List<NotifyCollectionChangedEventArgs> changes = [];
        list.CollectionChanged += (_, args) => changes.Add(args);

        list.UpdateRanges(new VirtualizingList<Row>.IndexRange(100_000, 20), []);
        list[100_000].IsPlaceholder.Should().BeTrue();

        source.Complete(0);

        await WaitForAsync(() => !list[100_000].IsPlaceholder);
        await WaitForAsync(() => changes.Count != 0);

        list[100_000].IsPlaceholder.Should().BeFalse();
        changes.Should().ContainSingle(change =>
            change.Action == NotifyCollectionChangedAction.Replace && change.NewStartingIndex == 100_000);
    }

    [Fact]
    public async Task Replaces_visible_items_even_when_winui_requested_them_before_reporting_the_range()
    {
        DeferredRangeSource source = new(500_000);
        using VirtualizingList<Row> list = CreateList(source);
        List<NotifyCollectionChangedEventArgs> changes = [];
        list.CollectionChanged += (_, args) => changes.Add(args);

        // This is the ListView ordering that caused the stale “Fetching” cells:
        // an indexer request arrives before IItemsRangeInfo.RangesChanged.
        list[100_000].IsPlaceholder.Should().BeTrue();
        list.UpdateRanges(new VirtualizingList<Row>.IndexRange(100_000, 20), []);
        source.Complete(0);

        await WaitForAsync(() => changes.Count != 0);

        changes.Should().ContainSingle(change =>
            change.Action == NotifyCollectionChangedAction.Replace && change.NewStartingIndex == 100_000);
        list[100_000].IsPlaceholder.Should().BeFalse();
    }

    [Fact]
    public async Task Drops_a_late_response_after_the_viewport_has_moved()
    {
        DeferredRangeSource source = new(500_000);
        using VirtualizingList<Row> list = CreateList(source);

        list.UpdateRanges(new VirtualizingList<Row>.IndexRange(100_000, 20), []);
        list.UpdateRanges(new VirtualizingList<Row>.IndexRange(300_000, 20), []);

        source.Requests.Should().HaveCount(2);
        source.CancellationTokens[0].IsCancellationRequested.Should().BeTrue();

        source.Complete(1);
        source.Complete(0);

        await WaitForAsync(() => !list[300_000].IsPlaceholder);

        list[300_000].IsPlaceholder.Should().BeFalse();
        list[100_000].IsPlaceholder.Should().BeTrue();
        list.CachedItemCount.Should().Be(420);
    }

    private static VirtualizingList<Row> CreateList(IAsyncRangeSource<Row> source) => new(
        source,
        static index => new Row(index, true),
        ImmediateStateScheduler.Instance);

    private static async Task WaitForAsync(Func<bool> condition)
    {
        for (int attempt = 0; attempt < 100; attempt++)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(10);
        }

        condition().Should().BeTrue("the asynchronous range load should have completed");
    }

    private sealed record Row(int Index, bool IsPlaceholder);

    private readonly record struct RequestedRange(int StartIndex, int Count);

    private sealed class RecordingRangeSource : IAsyncRangeSource<Row>
    {
        public RecordingRangeSource(int count) => Count = count;

        public int Count { get; }

        public List<RequestedRange> Requests { get; } = [];

        public Task<IReadOnlyList<Row>> LoadRangeAsync(int startIndex, int count, CancellationToken cancellationToken)
        {
            Requests.Add(new RequestedRange(startIndex, count));
            IReadOnlyList<Row> rows = Enumerable.Range(startIndex, count)
                .Select(index => new Row(index, false))
                .ToArray();
            return Task.FromResult(rows);
        }
    }

    private sealed class DeferredRangeSource : IAsyncRangeSource<Row>
    {
        private readonly List<TaskCompletionSource<IReadOnlyList<Row>>> _completions = [];

        public DeferredRangeSource(int count) => Count = count;

        public int Count { get; }

        public List<RequestedRange> Requests { get; } = [];

        public List<CancellationToken> CancellationTokens { get; } = [];

        public Task<IReadOnlyList<Row>> LoadRangeAsync(int startIndex, int count, CancellationToken cancellationToken)
        {
            Requests.Add(new RequestedRange(startIndex, count));
            CancellationTokens.Add(cancellationToken);
            TaskCompletionSource<IReadOnlyList<Row>> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _completions.Add(completion);
            return completion.Task;
        }

        public void Complete(int requestIndex)
        {
            RequestedRange request = Requests[requestIndex];
            IReadOnlyList<Row> rows = Enumerable.Range(request.StartIndex, request.Count)
                .Select(index => new Row(index, false))
                .ToArray();
            _completions[requestIndex].SetResult(rows);
        }
    }
}
