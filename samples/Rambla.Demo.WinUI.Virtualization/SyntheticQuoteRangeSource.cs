using Rambla.WinUI.Virtualization;

namespace Rambla.Demo.WinUI.Virtualization;

/// <summary>Generates rows on demand so the sample never allocates 500,000 models.</summary>
internal sealed class SyntheticQuoteRangeSource : IAsyncRangeSource<QuoteRow>
{
    public SyntheticQuoteRangeSource(int count) => Count = count;

    public int Count { get; }

    public async Task<IReadOnlyList<QuoteRow>> LoadRangeAsync(
        int startIndex,
        int count,
        CancellationToken cancellationToken)
    {
        // Makes loading placeholders observable when rapidly dragging the thumb.
        await Task.Delay(TimeSpan.FromMilliseconds(35), cancellationToken);

        QuoteRow[] rows = new QuoteRow[count];
        for (int offset = 0; offset < count; offset++)
        {
            int index = startIndex + offset;
            decimal mid = 90m + ((index * 73) % 50_000) / 100m;
            rows[offset] = new QuoteRow(
                index,
                $"RMB-{index % 10_000:D4}",
                (mid - 0.015m).ToString("F3"),
                (mid + 0.007m).ToString("F3"),
                "Loaded");
        }

        return rows;
    }
}

internal sealed record QuoteRow(int Index, string Symbol, string Bid, string Last, string Status)
{
    public static QuoteRow Loading(int index) => new(index, "Loading…", "—", "—", "Fetching");
}
