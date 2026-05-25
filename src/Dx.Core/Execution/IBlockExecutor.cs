using Dx.Core.Protocol;

namespace Dx.Core.Execution;

/// <summary>
/// Contract for a single-responsibility block executor.
/// Each implementation handles exactly one <see cref="BlockType"/>,
/// catches all exceptions internally, and returns <see cref="OperationResult"/> — never throws.
/// </summary>
internal interface IBlockExecutor
{
    /// <summary>The block category this executor handles.</summary>
    BlockType Handles { get; }

    /// <summary>
    /// Executes <paramref name="block"/> within the supplied <paramref name="ctx"/>.
    /// </summary>
    /// <returns>
    /// An <see cref="OperationResult"/> describing the outcome.
    /// <see cref="OperationResult.Success"/> is <see langword="false"/> on failure;
    /// exceptions are never propagated to the caller.
    /// </returns>
    Task<OperationResult> ExecuteAsync(DxBlock block, BlockExecutionContext ctx, CancellationToken ct);
}
