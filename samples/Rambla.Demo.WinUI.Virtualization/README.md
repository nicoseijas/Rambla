# WinUI data-virtualization proof of concept

This app binds a `VirtualizingList<QuoteRow>` with **500,000 logical rows** to
WinUI `ListView`. The source creates only the requested quote slice; the list
retains at most **420** loading or loaded rows (20 visible rows plus a 200-row
buffer before and after).

Those values are illustrative. The consuming application chooses
`prefetchBefore`, `prefetchAfter`, and `maximumCachedItems` for each list. The
maximum should cover both buffers plus the expected number of visible rows; if
it does not, the list prioritizes the visible range and trims the buffer.

Run it on 64-bit Windows with the .NET 10 SDK:

```powershell
dotnet run --project samples/Rambla.Demo.WinUI.Virtualization -c Release
```

Drag the scrollbar from top to bottom. Its thumb represents all 500,000 rows,
not the cache. Each row has a fixed 28 px height, which is necessary for that
logical-position mapping to stay exact.
