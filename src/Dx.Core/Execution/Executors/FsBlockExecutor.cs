using Dapper;

using Dx.Core.Protocol;
using Dx.Core.Storage;

namespace Dx.Core.Execution.Executors;

/// <summary>
/// Executes <see cref="FsBlock"/> filesystem operations (move, delete, encode, checkout, restore).
/// Logic copied verbatim from <c>DxDispatcher.ExecuteFsOpAsync</c>.
/// </summary>
internal sealed class FsBlockExecutor : IBlockExecutor
{
    /// <inheritdoc/>
    /// <remarks>
    /// Note that <c>FsBlock</c> is used for multiple different filesystem operations, so we need to handle all of them here instead of skipping some in the dispatcher.
    /// </remarks>
    public BlockType Handles => BlockType.Fs;

    /// <inheritdoc/>
    public async Task<OperationResult> ExecuteAsync(DxBlock block, BlockExecutionContext ctx, CancellationToken ct)
    {
        if (block is not FsBlock fs)
            return OperationResult.FailureResult("FS", null, "Invalid block type.");

        try
        {
            await ExecuteFsOpAsync(fs, ctx, ct);
            return OperationResult.SuccessResult($"FS:{fs.Op}", fs.Args.GetValueOrDefault("path") ?? fs.Args.GetValueOrDefault("snap"));
        }
        catch (Exception ex)
        {
            return OperationResult.FailureResult($"FS:{fs.Op}", fs.Args.GetValueOrDefault("path") ?? fs.Args.GetValueOrDefault("snap"), ex.Message);
        }
    }

    private static async Task ExecuteFsOpAsync(FsBlock fs, BlockExecutionContext ctx, CancellationToken ct)
    {
        switch (fs.Op)
        {
            case "move":
                {
                    var from = DxBlockHelpers.ResolveAndValidate(fs.Args.GetValueOrDefault("from", string.Empty), ctx.WorkspaceRoot);
                    var to = DxBlockHelpers.ResolveAndValidate(fs.Args.GetValueOrDefault("to", string.Empty), ctx.WorkspaceRoot);
                    Directory.CreateDirectory(Path.GetDirectoryName(to)!);
                    File.Move(from, to, overwrite: true);
                    ctx.Logger.Debug($"  move {fs.Args["from"]} → {fs.Args["to"]}");
                    break;
                }
            case "delete":
                {
                    var path = DxBlockHelpers.ResolveAndValidate(fs.Args.GetValueOrDefault("path", string.Empty), ctx.WorkspaceRoot);
                    var recursive = fs.Args.GetValueOrDefault("recursive", "false") == "true";
                    var ifExists = fs.Args.GetValueOrDefault("if-exists", "false") == "true";

                    if (Directory.Exists(path))
                    {
                        if (!recursive)
                            throw new DxException(DxError.InvalidArgument, $"Cannot delete directory without recursive=true: {fs.Args["path"]}");
                        Directory.Delete(path, recursive: true);
                    }
                    else if (File.Exists(path))
                        File.Delete(path);
                    else if (!ifExists)
                        throw new DxException(DxError.InvalidArgument, $"Path not found: {fs.Args["path"]}");

                    ctx.Logger.Debug($"  delete {fs.Args["path"]}");
                    break;
                }
            case "encode":
                {
                    var path = DxBlockHelpers.ResolveAndValidate(fs.Args.GetValueOrDefault("path", string.Empty), ctx.WorkspaceRoot);
                    var toEncoding = fs.Args.GetValueOrDefault("to", "utf-8");
                    var lineEndings = fs.Args.GetValueOrDefault("line-endings", "preserve");

                    var bytes = await File.ReadAllBytesAsync(path, ct);
                    var srcEnc = DxBlockHelpers.DetectEncoding(bytes);
                    var text = srcEnc.GetString(DxBlockHelpers.StripBom(bytes, srcEnc));

                    if (lineEndings != "preserve")
                        text = DxBlockHelpers.NormalizeLineEndings(text, lineEndings);

                    var (dstEnc, addBom) = DxBlockHelpers.ResolveEncodingWithBom(toEncoding);
                    var outBytes = dstEnc.GetBytes(text);

                    using var fs2 = File.OpenWrite(path);
                    if (addBom)
                    {
                        var bom = dstEnc.GetPreamble();
                        await fs2.WriteAsync(bom, ct);
                    }
                    await fs2.WriteAsync(outBytes, ct);
                    fs2.SetLength(fs2.Position);

                    ctx.Logger.Debug($"  encode {fs.Args["path"]} → {toEncoding}");
                    break;
                }
            case "checkout":
                {
                    var snapHandle = fs.Args.GetValueOrDefault("snap", string.Empty)
                        ?? throw new DxException(DxError.InvalidArgument, "checkout requires snap= argument");

                    var snapHash = HandleAssigner.Resolve(ctx.Connection, ctx.SessionId, snapHandle)
                        ?? throw new DxException(DxError.SnapNotFound, $"Snap not found: {snapHandle}");

                    var engine = new RollbackEngine(ctx.Connection, ctx.WorkspaceRoot, ctx.IgnoreSet);
                    await Task.Run(() => engine.RestoreTo(snapHash), ct);

                    ctx.Logger.Debug($"  checkout {snapHandle}");
                    break;
                }
            case "restore":
                {
                    var path = DxBlockHelpers.ResolveAndValidate(fs.Args.GetValueOrDefault("path", string.Empty), ctx.WorkspaceRoot);
                    var snapHandle = fs.Args.GetValueOrDefault("snap", string.Empty)
                        ?? throw new DxException(DxError.InvalidArgument, "restore requires snap= argument");

                    var snapHash = HandleAssigner.Resolve(ctx.Connection, ctx.SessionId, snapHandle)
                        ?? throw new DxException(DxError.SnapNotFound, $"Snap not found: {snapHandle}");

                    var relPath = DxPath.Normalize(ctx.WorkspaceRoot, path);
                    var fileHash = await ctx.Connection.ExecuteScalarAsync<byte[]>(
                        new CommandDefinition(
                            "SELECT content_hash FROM snap_files WHERE snap_hash = @sh AND path = @path",
                            new { sh = snapHash, path = relPath }, cancellationToken: ct));

                    if (fileHash is null)
                        throw new DxException(DxError.SnapNotFound, $"File {relPath} not found in snap {snapHandle}");

                    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                    var store = new SqliteContentStore(ctx.Connection);
                    using var src = store.OpenRead(fileHash);
                    using var dst = File.OpenWrite(path);
                    await src.CopyToAsync(dst, ct);
                    dst.SetLength(dst.Position);
                    ctx.Logger.Debug($"  restore {relPath} from {snapHandle}");
                    break;
                }
            default:
                throw new DxException(DxError.ParseError, $"Unknown FS op: {fs.Op}");
        }
    }
}
