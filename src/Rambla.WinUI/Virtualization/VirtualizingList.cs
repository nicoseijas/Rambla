using System.Collections;
using System.Collections.Specialized;
using System.ComponentModel;
using Microsoft.UI.Xaml.Data;
using Rambla.Scheduling;

namespace Rambla.WinUI.Virtualization;

/// <summary>
/// A read-only WinUI items source with a full logical count and a bounded,
/// asynchronously populated item window.
/// </summary>
/// <typeparam name="T">The item type exposed to WinUI.</typeparam>
/// <remarks>
/// <para>
/// Bind this directly to <c>ListView.ItemsSource</c>. Its <see cref="Count"/>
/// remains the source's complete logical count, so ListView keeps an accurately
/// scaled scroll bar. <see cref="IItemsRangeInfo"/> tells this list which items
/// WinUI is displaying; only those items and the configured nearby buffer are
/// retained.
/// </para>
/// <para>
/// Use a fixed-height item template when the scroll thumb must map exactly to
/// logical indices. Variable item heights are a layout concern that WinUI cannot
/// infer from an unloaded item source.
/// </para>
/// </remarks>
public sealed class VirtualizingList<T> : IList, IReadOnlyList<T>, IItemsRangeInfo, INotifyCollectionChanged, INotifyPropertyChanged, IDisposable
{
    private readonly IAsyncRangeSource<T> _source;
    private readonly int _count;
    private readonly Func<int, T> _placeholderFactory;
    private readonly IStateScheduler _scheduler;
    private readonly object _gate = new();
    private readonly Dictionary<int, T> _items = new();
    private readonly HashSet<int> _loadedIndexes = [];
    private readonly HashSet<int> _observedIndexes = [];
    private HashSet<int> _visibleIndexes = [];
    private CancellationTokenSource? _activeRequest;
    private HashSet<int> _retainedIndexes = [];
    private long _generation;
    private bool _disposed;
    private Exception? _lastLoadException;

    /// <summary>
    /// Creates a virtualized, read-only items source.
    /// </summary>
    /// <param name="source">The source that fetches logical slices.</param>
    /// <param name="placeholderFactory">
    /// Produces a non-null loading item for an unloaded logical index. Its shape
    /// should be bindable by the same item template as a loaded item.
    /// </param>
    /// <param name="scheduler">
    /// The UI scheduler that applies loaded values and raises collection change
    /// notifications. Pass a <see cref="DispatcherStateScheduler"/> for WinUI.
    /// </param>
    /// <param name="prefetchBefore">Logical items retained before the visible range.</param>
    /// <param name="prefetchAfter">Logical items retained after the visible range.</param>
    /// <param name="maximumCachedItems">Maximum placeholders and loaded items retained by this list.</param>
    public VirtualizingList(
        IAsyncRangeSource<T> source,
        Func<int, T> placeholderFactory,
        IStateScheduler scheduler,
        int prefetchBefore = 200,
        int prefetchAfter = 200,
        int maximumCachedItems = 420)
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));
        _placeholderFactory = placeholderFactory ?? throw new ArgumentNullException(nameof(placeholderFactory));
        _scheduler = scheduler ?? throw new ArgumentNullException(nameof(scheduler));

        if (source.Count < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(source), "The logical item count cannot be negative.");
        }

        _count = source.Count;

        ArgumentOutOfRangeException.ThrowIfNegative(prefetchBefore);
        ArgumentOutOfRangeException.ThrowIfNegative(prefetchAfter);
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumCachedItems, 1);

        PrefetchBefore = prefetchBefore;
        PrefetchAfter = prefetchAfter;
        MaximumCachedItems = maximumCachedItems;
    }

    /// <inheritdoc />
    public event NotifyCollectionChangedEventHandler? CollectionChanged;

    /// <inheritdoc />
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Gets the number of logical items, not just the current cache.</summary>
    public int Count => _count;

    /// <summary>Gets the number of cached placeholders and loaded items.</summary>
    public int CachedItemCount
    {
        get
        {
            lock (_gate)
            {
                return _items.Count;
            }
        }
    }

    /// <summary>Gets the number of items retained ahead of the visible range.</summary>
    public int PrefetchBefore { get; }

    /// <summary>Gets the number of items retained after the visible range.</summary>
    public int PrefetchAfter { get; }

    /// <summary>Gets the upper bound for the local cache.</summary>
    public int MaximumCachedItems { get; }

    /// <summary>
    /// Gets the most recent range-load failure for the current viewport, if any.
    /// A later viewport request clears this value before it starts loading.
    /// </summary>
    public Exception? LastLoadException
    {
        get
        {
            lock (_gate)
            {
                return _lastLoadException;
            }
        }
    }

    /// <summary>Gets an item at its logical index.</summary>
    public T this[int index] => GetItemAt(index);

    /// <summary>Gets a logical item, creating its loading placeholder if necessary.</summary>
    public T GetItemAt(int index)
    {
        ValidateIndex(index);

        lock (_gate)
        {
            ThrowIfDisposed();
            if (_items.TryGetValue(index, out T? item))
            {
                _observedIndexes.Add(index);
                return item;
            }

            T placeholder = _placeholderFactory(index);
            if (placeholder is null)
            {
                throw new InvalidOperationException("The placeholder factory must not return null.");
            }

            if (_retainedIndexes.Contains(index))
            {
                _items[index] = placeholder;
                _observedIndexes.Add(index);
            }

            return placeholder;
        }
    }

    /// <summary>
    /// Receives the visible and tracked ranges from ListView and refreshes the
    /// bounded cache around them. Calls that become stale are cancelled.
    /// </summary>
    public void RangesChanged(ItemIndexRange visibleRange, IReadOnlyList<ItemIndexRange> trackedItems)
    {
        ArgumentNullException.ThrowIfNull(trackedItems);

        IndexRange visible = new(visibleRange.FirstIndex, visibleRange.Length);
        IndexRange[] tracked = trackedItems
            .Select(range => new IndexRange(range.FirstIndex, range.Length))
            .ToArray();
        UpdateRanges(visible, tracked);
    }

    internal void UpdateRanges(IndexRange visibleRange, IReadOnlyList<IndexRange> trackedItems)
    {
        ArgumentNullException.ThrowIfNull(trackedItems);

        List<RangeRequest> missingRanges;
        CancellationToken cancellationToken;
        long generation;

        lock (_gate)
        {
            ThrowIfDisposed();

            HashSet<int> retained = BuildRetainedIndexes(visibleRange, trackedItems);
            _retainedIndexes = retained;
            _visibleIndexes = BuildVisibleIndexes(visibleRange, retained);

            foreach (int index in _items.Keys.Where(index => !retained.Contains(index)).ToArray())
            {
                _items.Remove(index);
                _loadedIndexes.Remove(index);
                _observedIndexes.Remove(index);
            }

            foreach (int index in retained)
            {
                if (!_items.ContainsKey(index))
                {
                    T placeholder = _placeholderFactory(index);
                    if (placeholder is null)
                    {
                        throw new InvalidOperationException("The placeholder factory must not return null.");
                    }

                    _items[index] = placeholder;
                }
            }

            missingRanges = FindMissingRanges(retained);
            _activeRequest?.Cancel();
            _activeRequest?.Dispose();
            _activeRequest = new CancellationTokenSource();
            cancellationToken = _activeRequest.Token;
            generation = ++_generation;
            _lastLoadException = null;
        }

        OnPropertyChanged(nameof(CachedItemCount));
        OnPropertyChanged(nameof(LastLoadException));
        _ = LoadMissingRangesAsync(missingRanges, generation, cancellationToken);
    }

    /// <inheritdoc />
    object? IList.this[int index]
    {
        get => GetItemAt(index);
        set => ThrowReadOnly();
    }

    /// <inheritdoc />
    bool IList.IsReadOnly => true;

    /// <inheritdoc />
    bool IList.IsFixedSize => true;

    /// <inheritdoc />
    int ICollection.Count => Count;

    /// <inheritdoc />
    bool ICollection.IsSynchronized => false;

    /// <inheritdoc />
    object ICollection.SyncRoot => _gate;

    /// <inheritdoc />
    int IList.Add(object? value) => ThrowReadOnly<int>();

    /// <inheritdoc />
    void IList.Clear() => ThrowReadOnly();

    /// <inheritdoc />
    bool IList.Contains(object? value) => ((IList)this).IndexOf(value) >= 0;

    /// <inheritdoc />
    int IList.IndexOf(object? value)
    {
        lock (_gate)
        {
            foreach ((int index, T item) in _items)
            {
                if (Equals(item, value))
                {
                    return index;
                }
            }
        }

        return -1;
    }

    /// <inheritdoc />
    void IList.Insert(int index, object? value) => ThrowReadOnly();

    /// <inheritdoc />
    void IList.Remove(object? value) => ThrowReadOnly();

    /// <inheritdoc />
    void IList.RemoveAt(int index) => ThrowReadOnly();

    /// <inheritdoc />
    void ICollection.CopyTo(Array array, int index)
    {
        ArgumentNullException.ThrowIfNull(array);
        if (array.Rank != 1)
        {
            throw new ArgumentException("The destination array must be one-dimensional.", nameof(array));
        }

        if (index < 0 || index > array.Length - Count)
        {
            throw new ArgumentOutOfRangeException(nameof(index));
        }

        for (int itemIndex = 0; itemIndex < Count; itemIndex++)
        {
            array.SetValue(GetItemAt(itemIndex), index + itemIndex);
        }
    }

    /// <inheritdoc />
    public IEnumerator GetEnumerator()
    {
        for (int index = 0; index < Count; index++)
        {
            yield return GetItemAt(index);
        }
    }

    IEnumerator<T> IEnumerable<T>.GetEnumerator()
    {
        for (int index = 0; index < Count; index++)
        {
            yield return GetItemAt(index);
        }
    }

    /// <summary>Cancels outstanding loads and releases cached values.</summary>
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _activeRequest?.Cancel();
            _activeRequest?.Dispose();
            _activeRequest = null;
            _items.Clear();
            _loadedIndexes.Clear();
            _observedIndexes.Clear();
            _retainedIndexes.Clear();
            _visibleIndexes.Clear();
        }
    }

    private async Task LoadMissingRangesAsync(
        IReadOnlyList<RangeRequest> missingRanges,
        long generation,
        CancellationToken cancellationToken)
    {
        try
        {
            foreach (RangeRequest range in missingRanges)
            {
                IReadOnlyList<T> loaded = await _source.LoadRangeAsync(
                    range.StartIndex,
                    range.Count,
                    cancellationToken).ConfigureAwait(false);

                cancellationToken.ThrowIfCancellationRequested();
                if (loaded.Count != range.Count)
                {
                    throw new InvalidOperationException(
                        $"The range source returned {loaded.Count} items for a request of {range.Count} items.");
                }

                _scheduler.Post(() => ApplyLoadedRange(range.StartIndex, loaded, generation));
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // A new viewport superseded this one; it owns the next request.
        }
        catch (Exception exception)
        {
            _scheduler.Post(() => RecordLoadFailure(exception, generation));
        }
    }

    private void ApplyLoadedRange(int startIndex, IReadOnlyList<T> loaded, long generation)
    {
        List<(int Index, T OldItem, T NewItem)> replacements = [];

        lock (_gate)
        {
            if (_disposed || generation != _generation)
            {
                return;
            }

            for (int offset = 0; offset < loaded.Count; offset++)
            {
                int index = startIndex + offset;
                if (!_retainedIndexes.Contains(index))
                {
                    continue;
                }

                T newItem = loaded[offset] ?? throw new InvalidOperationException("The range source must not return null items.");
                if (_items.TryGetValue(index, out T? oldItem))
                {
                    _items[index] = newItem;
                    _loadedIndexes.Add(index);
                    // ListView is allowed to request an item before it reports
                    // that item's range. Always replacing the current visible
                    // items closes that race; prefetched-only rows stay quiet.
                    if (_visibleIndexes.Contains(index) || _observedIndexes.Contains(index))
                    {
                        replacements.Add((index, oldItem, newItem));
                    }
                }
                else
                {
                    _items[index] = newItem;
                    _loadedIndexes.Add(index);
                }
            }
        }

        foreach ((int index, T oldItem, T newItem) in replacements)
        {
            CollectionChanged?.Invoke(
                this,
                new NotifyCollectionChangedEventArgs(
                    NotifyCollectionChangedAction.Replace,
                    newItem,
                    oldItem,
                    index));
        }

        OnPropertyChanged(nameof(CachedItemCount));
    }

    private void RecordLoadFailure(Exception exception, long generation)
    {
        lock (_gate)
        {
            if (_disposed || generation != _generation)
            {
                return;
            }

            _lastLoadException = exception;
        }

        OnPropertyChanged(nameof(LastLoadException));
    }

    private HashSet<int> BuildRetainedIndexes(IndexRange visibleRange, IReadOnlyList<IndexRange> trackedItems)
    {
        HashSet<int> retained = [];
        AddRange(retained, visibleRange, PrefetchBefore, PrefetchAfter);

        foreach (IndexRange trackedRange in trackedItems)
        {
            AddRange(retained, trackedRange, 0, 0);
        }

        if (retained.Count <= MaximumCachedItems)
        {
            return retained;
        }

        int visibleStart = ClampIndex(visibleRange.FirstIndex);
        int visibleEnd = ClampIndex((long)visibleRange.FirstIndex + visibleRange.Length - 1);
        int centre = visibleStart + ((visibleEnd - visibleStart) / 2);

        return retained
            .OrderBy(index => Math.Abs((long)index - centre))
            .ThenBy(index => index)
            .Take(MaximumCachedItems)
            .ToHashSet();
    }

    private HashSet<int> BuildVisibleIndexes(IndexRange visibleRange, HashSet<int> retained)
    {
        HashSet<int> visible = [];
        AddRange(visible, visibleRange, 0, 0);
        visible.IntersectWith(retained);
        return visible;
    }

    private void AddRange(HashSet<int> indexes, IndexRange range, int before, int after)
    {
        if (range.Length == 0 || Count == 0)
        {
            return;
        }

        int start = ClampIndex((long)range.FirstIndex - before);
        int end = ClampIndex((long)range.FirstIndex + range.Length - 1 + after);
        for (int index = start; index <= end; index++)
        {
            indexes.Add(index);
        }
    }

    private List<RangeRequest> FindMissingRanges(HashSet<int> retained)
    {
        List<RangeRequest> ranges = [];
        int? start = null;
        int previous = -2;

        foreach (int index in retained.Order())
        {
            if (_loadedIndexes.Contains(index))
            {
                if (start is not null)
                {
                    ranges.Add(new RangeRequest(start.Value, previous - start.Value + 1));
                    start = null;
                }

                continue;
            }

            if (start is null || index != previous + 1)
            {
                if (start is not null)
                {
                    ranges.Add(new RangeRequest(start.Value, previous - start.Value + 1));
                }

                start = index;
            }

            previous = index;
        }

        if (start is not null)
        {
            ranges.Add(new RangeRequest(start.Value, previous - start.Value + 1));
        }

        return ranges;
    }

    private int ClampIndex(long index) => (int)Math.Clamp(index, 0, (long)Count - 1);

    private void ValidateIndex(int index)
    {
        if ((uint)index >= (uint)Count)
        {
            throw new ArgumentOutOfRangeException(nameof(index));
        }
    }

    private void OnPropertyChanged(string propertyName)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    private static void ThrowReadOnly() => throw new NotSupportedException("VirtualizingList is read-only.");

    private static TResult ThrowReadOnly<TResult>()
    {
        ThrowReadOnly();
        return default!;
    }

    private readonly record struct RangeRequest(int StartIndex, int Count);

    internal readonly record struct IndexRange(int FirstIndex, uint Length);
}
