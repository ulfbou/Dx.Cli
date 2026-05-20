using Dx.Core.Execution;
using Dx.Core.Execution.Results;

using System.Threading.Tasks;

namespace Dx.Core.Protocol;

/// <summary>
/// Defines the transactional execution engine contract for DX.
/// </summary>
/// <remarks>
/// <para>
/// Implementations own the full transaction lifecycle:
/// locking, crash recovery, mutation, rollback, and snapshot creation.
/// </para>
/// <para>
/// Exceptions may be thrown by implementations. The protocol layer
/// (<see cref="Execution.DxProtocolDispatcher"/>) is responsible for
/// normalizing them into <see cref="Execution.Results.DxResult"/>.
/// </para>
/// </remarks>
public interface IDxDispatchEngine
{
    // NEW: Lifecycle Authority
    Task BeginPendingAsync(DxExecutionRequest request);
    Task CommitAsync(DxResult result);
    Task AbortAsync(DxResult result);

    /// <summary>
    /// Executes a DX request within a transactional boundary.
    /// </summary>
    /// <param name="request">
    /// The immutable execution request describing document, mode,
    /// progress sink, and cancellation.
    /// </param>
    /// <returns>
    /// A task that resolves to a non-null <see cref="DxResult"/>.
    /// </returns>
    Task<DxResult> DispatchAsync(DxExecutionRequest request);
}
