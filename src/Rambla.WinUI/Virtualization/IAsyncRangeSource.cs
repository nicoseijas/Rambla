namespace Rambla.WinUI.Virtualization;

/// <summary>
/// Provides slices of a logical, randomly accessible data set without requiring
/// the whole data set to be held by the UI process.
/// </summary>
/// <typeparam name="T">The item type consumed by the view.</typeparam>
public interface IAsyncRangeSource<T>
{
    /// <summary>
    /// Gets the total number of logical items. It must stay constant for the
    /// lifetime of a bound <see cref="VirtualizingList{T}"/>.
    /// </summary>
    int Count { get; }

    /// <summary>
    /// Loads exactly the requested contiguous range. The returned list must
    /// contain <paramref name="count"/> items unless cancellation was requested.
    /// </summary>
    /// <param name="startIndex">The zero-based logical index of the first item.</param>
    /// <param name="count">The number of items to load.</param>
    /// <param name="cancellationToken">Cancels a request superseded by scrolling.</param>
    /// <returns>The loaded items in logical-index order.</returns>
    Task<IReadOnlyList<T>> LoadRangeAsync(
        int startIndex,
        int count,
        CancellationToken cancellationToken);
}
