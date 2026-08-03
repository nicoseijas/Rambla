# Rambla.Avalonia

Avalonia dispatcher adapter for **[Rambla](https://www.nuget.org/packages/Rambla)** —
high-frequency observable state for real-time .NET desktop apps.

Rambla's core is framework-agnostic and never references a dispatcher. This
package supplies the `IStateScheduler` that marshals coalesced state flushes onto
the Avalonia UI thread, at `Background` priority so high-frequency state never
starves input or rendering.

Built against **Avalonia 11.0** — the minimum it supports. Apps on 11.x and 12.x
both work: the `IDispatcher` surface this adapter uses is unchanged across them.

## Usage

Install both packages, then install the scheduler once at startup:

```csharp
using Rambla.Avalonia;

public override void OnFrameworkInitializationCompleted()
{
    // Every RamblaState created without an explicit scheduler now flushes
    // onto the Avalonia UI thread.
    DispatcherStateScheduler.InstallAsDefault();

    base.OnFrameworkInitializationCompleted();
}
```

Your `RamblaState`-derived view models then update from any thread and bind
exactly like any `INotifyPropertyChanged` object — Rambla coalesces the
background writes into a few UI notifications per second.

### Capping the refresh rate

`InstallAsDefault` posts a flush as fast as the dispatcher drains its queue. To
bound it, install the throttled variant instead — it wraps the adapter in the
core's `ThrottlingStateScheduler`:

```csharp
// 60 flushes/second at most; omit the argument to use RamblaOptions.MaxRefreshRate.
_scheduler = DispatcherStateScheduler.InstallThrottledAsDefault(60);
```

It owns a timer, so keep the returned instance and dispose it on exit (after the
background writers stop).

### Testing

The adapter takes an `IDispatcher`, not the concrete `Dispatcher`, so a test can
substitute a fake and assert what reached the UI without a live UI thread.

## Links

- **Repository & docs:** https://github.com/nicoseijas/Rambla
- **Getting started:** https://github.com/nicoseijas/Rambla/wiki/Getting-Started
- **Frozen V1 semantics:** https://github.com/nicoseijas/Rambla/blob/main/SEMANTICS.md

Released under the [MIT License](https://github.com/nicoseijas/Rambla/blob/main/LICENSE).
