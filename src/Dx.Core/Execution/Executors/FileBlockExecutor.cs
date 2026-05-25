using Dx.Core.Protocol;

using System.Reflection.Metadata;

namespace Dx.Core.Execution.Executors;

/// <summary>
/// Executes <see cref="FileBlock"/> write operations.
/// Logic copied verbatim from <c>DxDispatcher.WriteFileAsync</c>.
/// </summary>
internal sealed class FileBlockExecutor : IBlockExecutor
{
    /// <inheritdoc/>
    /// <remarks>
    /// Note that <c>FileBlock</c> is used for both read-only checks and actual writes, so we need to handle the read-only case here instead of skipping it in the dispatcher.
    /// </remarks>
    public BlockType Handles => BlockType.File;


    /// <inheritdoc/>
    public async Task<OperationResult> ExecuteAsync(DxBlock block, BlockExecutionContext ctx, CancellationToken ct)
    {
        if (block is not FileBlock fb || fb.ReadOnly)
            return OperationResult.SuccessResult("FILE", null, "skipped (read-only)");

        try
        {
            await WriteFileAsync(fb, ctx, ct);
            return OperationResult.SuccessResult("FILE", fb.Path);
        }
        catch (Exception ex)
        {
            return OperationResult.FailureResult("FILE", fb.Path, ex.Message);
        }
    }

    private static async Task WriteFileAsync(FileBlock fb, BlockExecutionContext ctx, CancellationToken ct)
    {
        var absPath = DxBlockHelpers.ResolveAndValidate(fb.Path, ctx.WorkspaceRoot);

        if (!fb.Create && !File.Exists(absPath))
            throw new DxException(DxError.InvalidArgument, $"File does not exist and create=false: {fb.Path}");

        if (fb.IfContains is { } ic && File.Exists(absPath))
        {
            var content = await File.ReadAllTextAsync(absPath, ct);
            if (!content.Contains(ic, StringComparison.Ordinal))
                throw new DxException(DxError.InvalidArgument, $"Precondition failed (if-contains): \"{ic}\" not found in {fb.Path}");
        }

        if (fb.IfLine is { } il)
        {
            var parts = il.Split(':', 2);
            if (parts.Length == 2 && int.TryParse(parts[0], out var lineNo) && File.Exists(absPath))
            {
                var lines = await File.ReadAllLinesAsync(absPath, ct);
                if (lineNo < 1 || lineNo > lines.Length || !lines[lineNo - 1].Contains(parts[1], StringComparison.Ordinal))
                    throw new DxException(DxError.InvalidArgument, $"Precondition failed (if-line): line {lineNo} does not match \"{parts[1]}\"");
            }
        }

        Directory.CreateDirectory(Path.GetDirectoryName(absPath)!);
        var encoding = DxBlockHelpers.ResolveEncoding(fb.Encoding);
        await File.WriteAllTextAsync(absPath, fb.Content, encoding, ct);

        ctx.Logger.Debug($"  write {fb.Path}");
    }
}
