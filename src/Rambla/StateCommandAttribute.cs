namespace Rambla;

/// <summary>
/// Marks an async method so the Rambla source generator emits an
/// <see cref="AsyncStateCommand"/> for it, together with the state that describes
/// its run: a busy flag, the last error, and a cancel command. The method must
/// return <see cref="System.Threading.Tasks.Task"/> and take either no parameters
/// or a single <see cref="System.Threading.CancellationToken"/>.
/// </summary>
/// <remarks>
/// <para>
/// A trailing <c>Async</c> is stripped, so <c>RefreshAsync</c> yields
/// <c>RefreshCommand</c>, <c>IsRefreshing</c>, <c>RefreshError</c> and
/// <c>CancelRefreshCommand</c>.
/// </para>
/// <para>
/// The busy name is derived by an English <c>-ing</c> rule that covers the common
/// verbs (<c>Save</c> → <c>IsSaving</c>, <c>Submit</c> → <c>IsSubmitting</c>).
/// Where it reads wrong, set <see cref="BusyName"/> explicitly.
/// </para>
/// </remarks>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false, Inherited = false)]
public sealed class StateCommandAttribute : Attribute
{
    /// <summary>
    /// Latest-wins: when <see langword="true"/>, invoking the command while a run
    /// is in flight cancels that run and starts a new one (as-you-type search).
    /// When <see langword="false"/> (the default) the command refuses to start a
    /// second run, so the bound button disables itself while it is busy.
    /// </summary>
    public bool CancelPrevious { get; set; }

    /// <summary>
    /// Overrides the base name every generated member is built from. Defaults to
    /// the method name without its trailing <c>Async</c>.
    /// </summary>
    public string? Name { get; set; }

    /// <summary>
    /// Overrides the generated busy property name (default <c>Is{Name}ing</c>) —
    /// use it when the derived gerund reads wrong.
    /// </summary>
    public string? BusyName { get; set; }

    /// <summary>Overrides the generated error property name (default <c>{Name}Error</c>).</summary>
    public string? ErrorName { get; set; }
}
