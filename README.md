# Rambla

[![CI](https://github.com/nicoseijas/RamblaState/actions/workflows/ci.yml/badge.svg)](https://github.com/nicoseijas/RamblaState/actions/workflows/ci.yml)
[![NuGet](https://img.shields.io/nuget/v/Rambla?logo=nuget&label=NuGet)](https://www.nuget.org/packages/Rambla)
[![Downloads](https://img.shields.io/nuget/dt/Rambla?label=downloads)](https://www.nuget.org/packages/Rambla)
[![License: MIT](https://img.shields.io/badge/license-MIT-blue.svg)](./LICENSE)
[![.NET](https://img.shields.io/badge/.NET-netstandard2.0%20%7C%20net10.0-512BD4?logo=dotnet)](https://github.com/nicoseijas/RamblaState)

> **High-frequency observable state for real-time .NET desktop applications.**
>
> Thread-safe updates, batching, coalescing and diagnostics for UI state that
> changes faster than it can be rendered.

Named after the Uruguayan *rambla* — many parallel flows moving continuously
along a shared surface. That is exactly the problem Rambla solves: hundreds or
thousands of state changes per second, arriving from background threads, that
must reach the UI **without saturating it**.

---

## The problem

WPF and other XAML frameworks have thread affinity around a single UI
`Dispatcher`. That thread processes input, layout and rendering. When a feed
pushes updates at high frequency, the naive path looks like this:

```
WebSocket / worker / timer
        ↓  2,000 updates/sec
ViewModel
        ↓
PropertyChanged × N
ObservableCollection change × N
Dispatcher.Invoke × N
        ↓
UI thread saturated
```

The problem is **not** `INotifyPropertyChanged`. The problem is emitting tens of
thousands of notifications the UI cannot possibly render, one dispatcher hop at
a time.

## The idea: mutation ≠ notification

Rambla separates *state mutation* from *UI notification*. You write state from
any thread; Rambla decides when and how the UI is told, coalescing intermediate
values and batching notifications into a single dispatcher hop per frame.

```csharp
public partial class MarketViewModel : RamblaState
{
    [State] private decimal _bid;   // generates an observable `Bid` property
    [State] private decimal _ask;   // → `Ask`
    [State] private decimal _pnl;   // → `PnL`
}
```

You annotate the backing field; the generator emits the observable property
(stripping the leading underscore, `_bid` → `Bid`) and routes its setter through
Rambla's batching and coalescing.

From any background worker:

```csharp
vm.Bid = 1.2345m;
vm.Ask = 1.2346m;
vm.PnL = 23m;
```

No `Dispatcher.Invoke`, no `OnPropertyChanged(...)`, no
`SynchronizationContext.Post(...)`. Rambla already knows where and when to
notify.

The work that *fetches* the state gets the same treatment. Annotate an async
method and the generator emits the command plus the state that describes its run:

```csharp
public partial class SearchViewModel : RamblaState
{
    // → SearchCommand, IsSearching, SearchError, CancelSearchCommand
    [StateCommand(CancelPrevious = true)]
    private async Task SearchAsync(CancellationToken token)
        => Results = await _api.SearchAsync(Query, token);
}
```

`CancelPrevious` is latest-wins: each keystroke cancels the request it replaces.
Failures land in `SearchError` instead of crashing an `async void` handler, and
cancelling is not a failure. Bind the button to `SearchCommand` — it disables
itself while the run is in flight.

Instead of `4 dispatcher calls → 4 PropertyChanged → 4 binding passes`, the
runtime does:

```
worker writes → dirty state → coalesce → UI flush (~16 ms) → batched notification
```

## What makes it different

**Shipping today:**

- **Coalescing** — latest value wins. A price that ticks five times in 5 ms
  notifies the UI once, with the final value.
- **Explicit batching** — coalesce writes into one *coherent notification pass*:
  bindings are never notified mid-batch, so the UI re-renders on the batch's
  final values together, not on a half-applied `Bid`/stale `Ask`. (This is
  notification coherence, not cross-thread state atomicity — use snapshots for
  that.)
- **`[State]` source generator** — annotate a backing field, get an observable
  property routed through the batching/coalescing engine.
- **`[StateCommand]` async commands** — annotate an async method, get an
  `AsyncStateCommand` plus the state that describes its run: busy flag, last
  error, and a cancel command. Opt into latest-wins with `CancelPrevious = true`
  (an as-you-type search cancels the request it replaces).
- **Framework-neutral scheduling** — the core never references `Dispatcher`;
  integration is an `IStateScheduler` adapter (WPF and Avalonia ship today).
- **Bounded refresh rate** — wrap any scheduler in `ThrottlingStateScheduler` (or
  call `DispatcherStateScheduler.InstallThrottledAsDefault()`) and flushes reach
  the UI at most `MaxRefreshRate` times a second, no matter how fast the feed
  writes. The first update after an idle period still goes through immediately.
- **Opt-in metrics** — turn on lifetime counters (`Metrics`) to see how many
  mutations coalesced away.
- **Live diagnostics** — attach `StateDiagnostics.Attach(vm)` (the
  `Rambla.Diagnostics` package) for rates, coalescing ratio, hot properties,
  dispatcher latency and actionable recommendations. See below.
- **High-frequency collections** — `RamblaList<T>` and `RamblaDictionary<K,V>`
  accept writes from any thread and coalesce a burst into the minimum
  `CollectionChanged` events per flush (`Batch`, `ReplaceSnapshot` with a minimal
  diff; latest-value-wins per key), instead of one per item.

**On the roadmap** (designed, not yet shipped — see [ROADMAP.md](./ROADMAP.md)):

- **Snapshots** *(planned)* — publish an immutable scalar-state snapshot as a
  single consistent unit; the cross-thread state-atomicity path. *(For
  collections, `RamblaList<T>.ReplaceSnapshot` ships today.)*
- **Per-property frequency policy** *(planned)* — a refresh rate chosen per
  property (`[State(UpdateRate = 10)]`) rather than per scheduler. *(The
  scheduler-wide ceiling, `MaxRefreshRate`, ships today.)*
- **Priorities** *(planned)* — a framework-neutral abstraction over dispatcher
  priority levels, so real-time data outranks background text.

## The flagship: diagnostics

Attach a session to any state (it's a pure observer — zero behaviour change) and
poll it. This turns the invisible cost of pushing background state to the UI into
something you can observe and prove:

```csharp
using var session = StateDiagnostics.Attach(viewModel);
Console.WriteLine(session.Snapshot());   // e.g. once a second
```

```
MarketViewModel
  Incoming state mutations  :   18,420 / sec
  UI notifications          :       58 / sec
  Coalescing                :   99.68 %
  Dispatcher hops           :       60 / sec
  Longest UI flush          :   2.8 ms
  UI thread budget          :     17 %

  ⚠ 'Positions' generated 14,281 notifications/sec. Recommendation: batch related
    writes with BeginUpdate(), or for a collection use Batch()/ReplaceSnapshot().
```

Dispatcher latency, hops and an accurate UI-thread budget come from wrapping your
scheduler with `DiagnosticsScheduler`; without it those lines are omitted and the
rest is derived from notification-raise time. For a lighter footprint, the core
also exposes lifetime coalescing counters via the opt-in `Metrics` property
([BENCHMARKS.md](./BENCHMARKS.md) shows them in use).

## Built for

Trading terminals · dashboards · telemetry · monitoring · poker tables · market
data · device/software status · WebSocket-driven apps · any UI receiving
hundreds or thousands of updates per second.

## Where Rambla fits

Rambla does **not** compete with:

- **CommunityToolkit.Mvvm** — keep using it for `ObservableObject`,
  observable-property generation and `RelayCommand`/`AsyncRelayCommand`.
- **ReactiveUI** — keep it for reactive composition and schedulers.
- **DynamicData** — keep it for `IChangeSet<T>` reactive collection queries.

Rambla owns a smaller, sharper problem:

> **Make the background → UI boundary safe and cheap under sustained,
> high-frequency load.**

## Packages

| Package             | Purpose                                        |
| ------------------- | ---------------------------------------------- |
| `Rambla`            | Framework-agnostic core state engine           |
| `Rambla.Diagnostics`| Live diagnostics (`StateDiagnostics.Attach`)   |
| `Rambla.Wpf`        | WPF dispatcher scheduler adapter               |
| `Rambla.Avalonia`   | Avalonia dispatcher scheduler adapter          |
| `Rambla.WinUI`      | WinUI 3 adapter *(planned)*                     |

The core never references `Dispatcher`. Framework integration is an adapter
behind `IStateScheduler`.

## Proof

The headline is architectural, not a multiplier:

> **100,000 mutations → ~101 effective notifications — a ~99.9% reduction.**

That collapse is what makes the downstream cost disappear. Indicative result at
100,000 writes with a UI-like subscriber (convert + fan-out), from
[BENCHMARKS.md](./BENCHMARKS.md):

| Path             | Notifications | Mean       | Allocated |
| ---------------- | ------------: | ---------: | --------: |
| Naive            |       100,000 | ~11.0 ms   | 21,871 KB |
| Rambla coalesced |         ~101  | ~1.0 ms    |     32 KB |

Same producer load; ~11× faster and ~680× fewer allocations because the work was
never emitted. **Honest caveat:** with a no-op subscriber the naive path wins on
raw CPU (Rambla adds bookkeeping), and with a high-entropy stream — thousands of
*distinct* properties per flush — coalescing can't help and Rambla loses. It is a
tool for state that repeats faster than it renders, not for one-shot fan-outs.
See [BENCHMARKS.md](./BENCHMARKS.md) and [docs/philosophy.md](./docs/philosophy.md).

Run the [market dashboard demo](./samples/Rambla.Demo.MarketDashboard) to watch
it live.

## Status

Early but real: the core state engine (writes, batching, coalescing, schedulers,
opt-in metrics) is implemented, its V1 semantics are **frozen**
([SEMANTICS.md](./SEMANTICS.md)), and it is covered by unit + concurrency stress
tests. The **WPF** and **Avalonia** adapters and the market dashboard demo run.
The **`[State]`** and **`[StateCommand]`** source generators ship and are
dogfooded by the demo. High-frequency collections (**`RamblaList<T>`**,
**`RamblaDictionary<K,V>`**), the **`Rambla.Diagnostics`** package, the
**throttling scheduler** (`MaxRefreshRate`) and **async state commands** are
shipped. Next: the remaining framework adapters (Phase 5). See
[ROADMAP.md](./ROADMAP.md) for phases and
[VISION.md](./VISION.md) for the thesis. Contributors: read
[GUIDELINES.md](./GUIDELINES.md) first.

## License

Rambla is released under the permissive [MIT License](./LICENSE), so it can be
adopted anywhere with minimal friction.

## Language

English is the primary language of this project (code, docs, issues, and
discussions) to keep the international community inclusive.
