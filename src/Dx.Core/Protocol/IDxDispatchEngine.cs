using System.Threading.Tasks;

using Dx.Core.Execution;

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
    /// <summary>
    /// Executes a DX request within a transactional boundary.
    /// </summary>
    /// <param name="request">
    /// The immutable execution request describing document, mode,
    /// progress sink, and cancellation.
    /// </param>
    /// <returns>
    /// A task that resolves to a non-null <see cref="DispatchResult"/>.
    /// </returns>
    Task<DispatchResult> DispatchAsync(DxExecutionRequest request);
}