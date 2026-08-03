# Rambla.Diagnostics

Live diagnostics for **[Rambla](https://www.nuget.org/packages/Rambla)** — turns the
invisible cost of pushing background state to the UI into something you can observe
and prove.

Attach a session to any `RamblaState` and poll it for a reading:

```csharp
using Rambla.Diagnostics;

using var session = StateDiagnostics.Attach(viewModel);

// ...later, e.g. once a second on a timer:
DiagnosticsSnapshot s = session.Snapshot();
Console.WriteLine(s);
```

```
MarketViewModel
  Incoming state mutations  :   18,420 / sec
  UI notifications          :       58 / sec
  Coalescing                :   99.68 %
  Longest UI flush          :   2.8 ms
  UI thread budget          :     17 %

  ⚠ 'Positions' generated 14,281 notifications/sec. Recommendation: batch related
    writes with BeginUpdate(), or for a collection use Batch()/ReplaceSnapshot().
```

## What you get

- **Rates over a window** — mutations/sec, notifications/sec (only events actually
  raised to a subscriber — zero while nothing is bound), flushes/sec, and the
  **coalescing ratio** (how many mutations coalescing dropped before the flush).
- **Hot-property detection** — which properties produce the most notification
  traffic, each with its own coalescing ratio.
- **Actionable recommendations** — e.g. "batch this" when a property floods the UI.
- **Dispatcher metrics** (optional) — wrap your scheduler with `DiagnosticsScheduler`
  to also measure dispatcher latency, hops, and an accurate UI-thread budget.

## How it works

Diagnostics attach a **pure observer** (`IStateProbe`) to the state. It does not
change any behaviour — notification order, coherence, coalescing, and exception
semantics are exactly as without it — and it costs nothing when detached. The
sampler is clock-driven (`IClock`), so rate math is deterministic and testable.

```csharp
// With dispatcher metrics:
var scheduler = new DiagnosticsScheduler(DispatcherStateScheduler.ForCurrent());
var vm = new MarketViewModel(scheduler);
using var session = StateDiagnostics.Attach(vm, scheduler);
```

## Links

- **Repository & docs:** https://github.com/nicoseijas/Rambla
- **Wiki:** https://github.com/nicoseijas/Rambla/wiki

Released under the [MIT License](https://github.com/nicoseijas/Rambla/blob/main/LICENSE).
