using System.Threading;
using System.Threading.Tasks;
using Rambla;

namespace Rambla.PackageSmoke;

/// <summary>
/// Compilation-only consumer of the packed Rambla package. Its generated members
/// prove the package carries a working analyzer, rather than relying on the
/// repository's generator ProjectReference.
/// </summary>
public partial class ConsumerViewModel : RamblaState
{
    [State] private decimal _price;

    [StateCommand(CancelPrevious = true)]
    private Task RefreshAsync(CancellationToken cancellationToken)
    {
        Price = 1m;
        return Task.CompletedTask;
    }
}
