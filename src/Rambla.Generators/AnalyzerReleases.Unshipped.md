; Unshipped analyzer release
; https://github.com/dotnet/roslyn-analyzers/blob/main/src/Microsoft.CodeAnalysis.Analyzers/ReleaseTrackingAnalyzers.Help.md

### New Rules

Rule ID | Category | Severity | Notes
--------|----------|----------|-------
RMB001  | Rambla   | Error    | Containing type must be partial
RMB002  | Rambla   | Error    | [State] requires an instance field
RMB003  | Rambla   | Error    | Generated property name collides
RMB004  | Rambla   | Error    | [State] does not support readonly or const fields
RMB005  | Rambla   | Error    | Containing type must derive from RamblaState
RMB006  | Rambla   | Error    | [StateCommand] requires an instance method
RMB007  | Rambla   | Error    | [StateCommand] method must return Task
RMB008  | Rambla   | Error    | [StateCommand] method has an unsupported signature
RMB009  | Rambla   | Error    | Generated command member name collides
