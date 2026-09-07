# Rambla.WinUI

WinUI 3 dispatcher adapter for **[Rambla](https://www.nuget.org/packages/Rambla)**.
It marshals coalesced state notifications to the UI thread through the Windows
App SDK `DispatcherQueue`, at low priority so a high-frequency feed does not
starve input or rendering.

## Usage

Install the scheduler once during application startup, on the WinUI UI thread:

```csharp
using Rambla.WinUI;

// App.xaml.cs, after the UI thread exists
DispatcherStateScheduler.InstallAsDefault();
```

Every `RamblaState` created without an explicit scheduler now flushes onto that
UI queue. For a sustained stream, install the throttled variant and retain the
returned scheduler until shutdown:

```csharp
_scheduler = DispatcherStateScheduler.InstallThrottledAsDefault(60);
```

The returned `ThrottlingStateScheduler` owns a timer. Dispose it after stopping
the background writers.

## Experimental data virtualization

`VirtualizingList<T>` is a read-only `IList` / `IItemsRangeInfo` items source for
large WinUI `ListView` data sets. Its `Count` is the source's complete logical
count, so the scroll bar continues to represent all records; it loads only the
reported viewport and a nearby bounded buffer.

```csharp
var rows = new VirtualizingList<QuoteRow>(
    api,
    index => QuoteRow.Loading(index),
    new DispatcherStateScheduler(DispatcherQueue),
    prefetchBefore: 200,
    prefetchAfter: 200,
    maximumCachedItems: 420);

quotesListView.ItemsSource = rows;
```

`api` implements `IAsyncRangeSource<QuoteRow>` and fetches exactly the requested
logical range. Its `Count` is fixed while the list is bound. The three numeric
arguments are consumer policy, not fixed library limits: choose a buffer based
on fetch latency and item weight. Set `maximumCachedItems` to at least the
visible rows plus both buffers; if it is smaller, visible rows are retained and
the buffer is trimmed. Use a fixed-height item template when the scrollbar needs
an exact index-to-position mapping. See the included
[`Rambla.Demo.WinUI.Virtualization`](../../samples/Rambla.Demo.WinUI.Virtualization/README.md)
proof of concept for a 500,000-row source.

## Links

- **Repository & docs:** https://github.com/nicoseijas/Rambla
- **Frozen semantics:** https://github.com/nicoseijas/Rambla/blob/main/SEMANTICS.md

Released under the [MIT License](https://github.com/nicoseijas/Rambla/blob/main/LICENSE).
