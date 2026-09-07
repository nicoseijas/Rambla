# Rambla WinUI 3 demo

A real Windows App SDK / WinUI 3 market dashboard. A synthetic background feed
writes five fields across hundreds of rows while Rambla coalesces those writes
onto the window's `DispatcherQueue` at the selected refresh rate.

## Run

On Windows with the .NET 10 SDK and Windows App SDK runtime available:

```powershell
dotnet run --project samples/Rambla.Demo.WinUI/Rambla.Demo.WinUI.csproj
```

Press **Start** and observe incoming mutations versus UI notifications, scheduler
posts, and coalescing. Increase the quote rate or lower refresh Hz to make the
backpressure visible.

The feed, row model and metrics are linked from the WPF demo because they are
framework-neutral; the XAML window and scheduler integration are native WinUI 3.
