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

## Links

- **Repository & docs:** https://github.com/nicoseijas/Rambla
- **Frozen semantics:** https://github.com/nicoseijas/Rambla/blob/main/SEMANTICS.md

Released under the [MIT License](https://github.com/nicoseijas/Rambla/blob/main/LICENSE).
