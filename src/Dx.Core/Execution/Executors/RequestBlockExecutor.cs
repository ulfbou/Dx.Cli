using Dx.Core.Protocol;

using System.Diagnostics;

namespace Dx.Core.Execution.Executors;

/// <summary>
/// Executes <see cref="RequestBlock"/> operations — both read-only informational requests
/// and process-execution gates (<c>type=run</c>).
/// Logic copied verbatim from <c>DxDispatcher.DispatchReadOnlyBlockAsync</c> and
/// <c>DxDispatcher.ExecuteRunAsync</c>.
/// </summary>
internal sealed class RequestBlockExecutor : IBlockExecutor
{
    /// <inheritdoc/>
    /// <remarks>
    /// Note that <c>RequestBlock</c> is used for multiple different request types, so we need to handle all of them here instead of skipping some in the dispatcher.
    /// </remarks>
    public BlockType Handles => BlockType.Request;

    /// <inheritdoc/>
    public async Task<OperationResult> ExecuteAsync(DxBlock block, BlockExecutionContext ctx, CancellationToken ct)
    {
        if (block is not RequestBlock req)
            return OperationResult.FailureResult("REQUEST", null, "Invalid block type.");

        try
        {
            switch (req.Type)
            {
                case "run":
                    var runTimeout = ctx.RunTimeoutSeconds ?? 0;
                    var (exit, output) = await ExecuteRunAsync(req.Body.Trim(), runTimeout, ctx.WorkspaceRoot, ct);
                    return OperationResult.Create("REQUEST:run", null, exit == 0, output);
                default:
                    return OperationResult.SuccessResult($"REQUEST:{req.Type}", null);
            }
        }
        catch (Exception ex)
        {
            return OperationResult.FailureResult($"REQUEST:{req.Type}", null, ex.Message);
        }
    }

    private static async Task<(int ExitCode, string Output)> ExecuteRunAsync(string command, int timeoutSeconds, string workspaceRoot, CancellationToken ct)
    {
        var (shell, args) = DxBlockHelpers.GetShell(command);

        var psi = new ProcessStartInfo
        {
            FileName = shell,
            Arguments = args,
            WorkingDirectory = workspaceRoot,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        using var proc = Process.Start(psi)!;

        using var cts = timeoutSeconds > 0
            ? new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds))
            : new CancellationTokenSource();

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, cts.Token);

        var stdout = proc.StandardOutput.ReadToEndAsync(linked.Token);
        var stderr = proc.StandardError.ReadToEndAsync(linked.Token);

        try
        {
            await proc.WaitForExitAsync(linked.Token);
        }
        catch (OperationCanceledException)
        {
            proc.Kill(entireProcessTree: true);
            if (cts.IsCancellationRequested && !ct.IsCancellationRequested)
            {
                return (124, $"Command timed out after {timeoutSeconds}s\n");
            }
            throw;
        }

        return (proc.ExitCode, (await stdout) + (await stderr));
    }
}
