# Rambla

**High-frequency observable state for real-time .NET desktop applications.**
Thread-safe updates, batching, coalescing and diagnostics for UI state that
changes faster than it can be rendered.

Rambla separates *state mutation* from *UI notification*. You write state from any
thread; Rambla decides when and how the UI is told — coalescing intermediate
values and batching notifications into a single dispatcher hop per frame.

```csharp
using Rambla;

public partial class MarketViewModel : RamblaState
{
    [State] private decimal _bid;   // generates an observable `Bid` property
    [State] private decimal _ask;   // → `Ask`
    [State] private decimal _pnl;   // → `PnL`
}
```

From any background worker — no `Dispatcher.Invoke`, no `OnPropertyChanged`:

```csharp
vm.Bid = 1.2345m;
vm.Ask = 1.2346m;
vm.PnL = 23m;
```

A price that ticks five times in 5 ms notifies the UI **once**, with the final
value. 100,000 mutations collapse to ~101 effective notifications — a ~99.9%
reduction.

## What ships in this package

- The `RamblaState` engine: thread-safe `SetField`, `BeginUpdate` batching,
  coalesced flush.
- The `[State]` source generator.
- `AsyncStateCommand` and the `[StateCommand]` generator: an async command with
  its run lifecycle — busy flag, captured error, cancel command, and optional
  latest-wins (`CancelPrevious`).
- `RamblaList<T>`: a high-frequency observable collection — thread-safe writes,
  one coalesced flush raising the minimum `CollectionChanged` events
  (`Batch`, `ReplaceSnapshot` with a minimal diff, single-`Reset` fallback).
- `RamblaDictionary<K,V>`: the keyed companion — insertion-ordered entries,
  latest-value-wins per key, same coalesced minimum-diff flush.
- `IStateScheduler` with immediate + synchronization-context implementations.
- `ThrottlingStateScheduler`: a decorator that caps flushes at
  `MaxRefreshRate` per second (default 60), releasing the first update after an
  idle period immediately and pacing bursts.
- Opt-in coalescing metrics.

The core is `netstandard2.0` and **never references `Dispatcher`**. For WPF, add
the **[`Rambla.Wpf`](https://www.nuget.org/packages/Rambla.Wpf)** adapter.

## When *not* to use it

Rambla is for state that repeats faster than it renders. With a no-op subscriber
the naive path wins on raw CPU, and for high-entropy streams (thousands of
*distinct* properties per flush) coalescing can't help. It is not an event bus —
if every intermediate value matters, use a queue/stream instead.

## Links

- **Repository & docs:** https://github.com/nicoseijas/RamblaState
- **Getting started:** https://github.com/nicoseijas/RamblaState/wiki/Getting-Started
- **Benchmarks:** https://github.com/nicoseijas/RamblaState/blob/main/BENCHMARKS.md
- **Frozen V1 semantics:** https://github.com/nicoseijas/RamblaState/blob/main/SEMANTICS.md
- **Roadmap:** https://github.com/nicoseijas/RamblaState/blob/main/ROADMAP.md

Released under the [MIT License](https://github.com/nicoseijas/RamblaState/blob/main/LICENSE).
Pre-1.0: the API may still change before 1.0.
