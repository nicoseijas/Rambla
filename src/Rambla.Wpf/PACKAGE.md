# Rambla.Wpf

WPF dispatcher adapter for **[Rambla](https://www.nuget.org/packages/Rambla)** —
high-frequency observable state for real-time .NET desktop apps.

Rambla's core is framework-agnostic and never references `Dispatcher`. This
package supplies the `IStateScheduler` that marshals coalesced state flushes onto
the WPF UI thread, at Background priority so high-frequency state never starves
input or rendering.

## Usage

Install both packages, then install the scheduler once at startup on the UI
thread:

```csharp
// App.xaml.cs
using Rambla.Wpf;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Every RamblaState created without an explicit scheduler now flushes
        // onto the WPF dispatcher.
        DispatcherStateScheduler.InstallAsDefault();
    }
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

## Links

- **Repository & docs:** https://github.com/nicoseijas/Rambla
- **Getting started (WPF):** https://github.com/nicoseijas/Rambla/wiki/Getting-Started
- **Market dashboard sample:** https://github.com/nicoseijas/Rambla/tree/main/samples/Rambla.Demo.MarketDashboard

Released under the [MIT License](https://github.com/nicoseijas/Rambla/blob/main/LICENSE).
