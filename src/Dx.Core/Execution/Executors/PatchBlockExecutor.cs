using Dx.Core.Protocol;

namespace Dx.Core.Execution.Executors;

/// <summary>
/// Executes <see cref="PatchBlock"/> diff-apply operations.
/// Logic copied verbatim from <c>DxDispatcher.ApplyPatchAsync</c>.
/// </summary>
internal sealed class PatchBlockExecutor : IBlockExecutor
{
    /// <inheritdoc/>
    /// <remarks>
    /// Note that <c>PatchBlock</c> is purely a patch-application operation and has no read-only mode, so we don't need to check for that here.
    /// </remarks>
    public BlockType Handles => BlockType.Patch;

    public async Task<OperationResult> ExecuteAsync(DxBlock block, BlockExecutionContext ctx, CancellationToken ct)
    {
        if (block is not PatchBlock pb)
            return OperationResult.FailureResult("PATCH", null, "Invalid block type.");

        try
        {
            await ApplyPatchAsync(pb, ctx, ct);
            return OperationResult.SuccessResult("PATCH", pb.Path, $"{pb.Hunks.Count} hunk(s)");
        }
        catch (Exception ex)
        {
            return OperationResult.FailureResult("PATCH", pb.Path, ex.Message);
        }
    }

    private static async Task ApplyPatchAsync(PatchBlock pb, BlockExecutionContext ctx, CancellationToken ct)
    {
        var absPath = DxBlockHelpers.ResolveAndValidate(pb.Path, ctx.WorkspaceRoot);

        if (!File.Exists(absPath))
            throw new DxException(DxError.InvalidArgument, $"Cannot patch non-existent file: {pb.Path}");

        var content = await File.ReadAllTextAsync(absPath, ct);
        var patched = PatchEngine.Apply(content, pb.Hunks);
        await File.WriteAllTextAsync(absPath, patched, ct);

        ctx.Logger.Debug($"  patch {pb.Path} ({pb.Hunks.Count} hunks)");
    }
}
