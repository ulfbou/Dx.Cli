using Dapper;

using Dx.Core.Execution;
using Dx.Core.Execution.Adapters;
using Dx.Core.Execution.Executors;
using Dx.Core.Execution.Extensions;
using Dx.Core.Execution.Results;
using Dx.Core.Storage;

using Microsoft.Data.Sqlite;

using System.Diagnostics;
using System.Text;

namespace Dx.Core.Protocol;

/// <summary>
/// The authoritative protocol engine responsible for the atomic execution 
/// of DX documents and the maintenance of the session audit trail.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Protocol Authority:</strong>
/// This type is the sole authority for executing DX documents and producing 
/// authoritative session audit records.
/// </para>
/// <para>
/// <strong>Invariants:</strong>
/// <list type="bullet">
/// <item>
/// <description>No execution may occur outside this dispatcher.</description>
/// </item>
/// <item>
/// <description>Every execution attempt produces exactly two audit log entries 
/// (input intent, execution result).</description>
/// </item>
/// <item>
/// <description>This type must not interpret CLI-specific semantics.</description>
/// </item>
/// </list>
/// </para>
/// </remarks>
public sealed class DxDispatcher
{
    private readonly SqliteConnection _connection;
    private readonly IDxStore _store;
    private readonly string _workspaceRoot;
    private readonly IgnoreSet _ignoreSet;
    private readonly string _sessionId;
    private readonly IDxLogger _logger;

    private readonly Dictionary<BlockType, IBlockExecutor> _executors;

    public DxDispatcher(
        SqliteConnection connection,
        IDxStore store,
        string workspaceRoot,
        IgnoreSet ignoreSet,
        string sessionId,
        IDxLogger? logger = null)
    {
        _connection = connection ?? throw new ArgumentNullException(nameof(connection));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _workspaceRoot = workspaceRoot ?? throw new ArgumentNullException(nameof(workspaceRoot));
        _ignoreSet = ignoreSet ?? throw new ArgumentNullException(nameof(ignoreSet));
        _sessionId = sessionId ?? throw new ArgumentNullException(nameof(sessionId));
        _logger = logger ?? NullDxLogger.Instance;

        _executors = new()
        {
            [BlockType.File] = new FileBlockExecutor(),
            [BlockType.Patch] = new PatchBlockExecutor(),
            [BlockType.Fs] = new FsBlockExecutor(),
            [BlockType.Request] = new RequestBlockExecutor(),
        };
    }

    private static BlockType GetBlockType(DxBlock block) => block switch
    {
        FileBlock => BlockType.File,
        PatchBlock => BlockType.Patch,
        FsBlock => BlockType.Fs,
        RequestBlock => BlockType.Request,
        _ => throw new InvalidOperationException($"No executor registered for block type '{block.GetType().Name}'.")
    };

    /// <summary>
    /// Dispatches a DX document from its raw text. This is the authoritative intake overload
    /// that owns parsing, validation, and all failure normalization.
    /// </summary>
    /// <param name="rawText">The raw DX document text to parse and execute.</param>
    /// <param name="direction">Accountability metadata identifying the source of the request (e.g. "user", "tool").</param>
    /// <param name="mode">The execution mode (<see cref="DxExecutionMode.Apply"/>, <see cref="DxExecutionMode.DryRun"/>, or <see cref="DxExecutionMode.Request"/>).</param>
    /// <param name="progress">An optional progress reporter.</param>
    /// <param name="onBaseMismatch">
    /// Overrides <c>conflict.on_base_mismatch</c> for this invocation.
    /// <c>"warn"</c> logs a warning and continues; <c>"reject"</c> (or <see langword="null"/>)
    /// aborts with base-mismatch. When <see langword="null"/>, the workspace configuration is used.
    /// </param>
    /// <param name="runTimeoutSeconds">
    /// Overrides <c>run.run_timeout</c> for this invocation. <c>0</c> means no timeout
    /// (uses the configured default). Overrides workspace configuration for this invocation only.
    /// </param>
    /// <param name="ct">A cancellation token.</param>
    /// <returns>A <see cref="DxResult"/> representing the normalized outcome.</returns>
    /// <remarks>
    /// <para>
    /// <strong>Dual-Log Invariant:</strong>
    /// This method guarantees exactly one intent log entry is written before any execution
    /// attempt, and exactly one result log entry for every exit path — including parse failures,
    /// validation failures, cancellation, and unexpected exceptions.
    /// </para>
    /// <para>
    /// <strong>Parse Authority:</strong>
    /// <see cref="DxParser"/> must not be called outside this method.
    /// Parse failures are normalized into a <see cref="DxResult"/> with
    /// <see cref="DxResultStatus.ValidationFailure"/> — they never throw or exit before
    /// the result log is written.
    /// </para>
    /// </remarks>
    public async Task<DxResult> DispatchAsync(
        string rawText,
        string direction,
        DxExecutionMode mode,
        IProgress<string>? progress = null,
        string? onBaseMismatch = null,
        int? runTimeoutSeconds = null,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(direction);

        // INTENTIONAL: Do not validate rawText for Checkout.
        // See Issue #50 — dispatcher synthesizes intent document.
        if (mode != DxExecutionMode.Checkout)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(rawText);
        }

        var isDryRun = mode == DxExecutionMode.DryRun;
        var intentDocument = rawText;

        if (mode == DxExecutionMode.Checkout)
        {
            // When Checkout is invoked via raw text, the dispatcher will synthesise
            // an intent document. If the caller provided a rawText containing the
            // handle, attempt to preserve readability; otherwise the dispatcher
            // will synthesise a standard intent document when writing the log.
            intentDocument = rawText;
        }

        await _store.WriteSessionLogAsync(new SessionLogEntry
        {
            SessionId = _sessionId,
            Direction = direction,
            Document = intentDocument,
            TxSuccess = null,
            SnapHandle = null
        }, CancellationToken.None);

        DxResult result = new(DxResultStatus.ExecutionFailure,
                "Unexpected dispatcher fault.", null, null, isDryRun);

        try
        {
            if (mode == DxExecutionMode.Checkout)
            {
                if (isDryRun)
                {
                    var diagnostics = new[] {
                        new DxDiagnostic("INVALID_MODE", "Checkout does not support DryRun mode.", DxDiagnosticSeverity.Error)
                    };

                    result = new DxResult(DxResultStatus.ValidationFailure, "Invalid execution mode combination.", null, diagnostics, isDryRun);
                }
                else
                {
                    // Extract handle from raw text for backward compat
                    // Expected format: "(checkout T0001)" or "%%FS op=\"checkout\" snap=\"T0001\""
                    string handle = ExtractCheckoutHandle(rawText)
                       ?? throw new DxException(DxError.InvalidArgument, "Checkout requires a handle");

                    // Create a document with an FsBlock for checkout so it routes through the mutating transaction path
                    var checkoutBlock = new FsBlock(
                        Op: "checkout",
                        Args: new Dictionary<string, string> { { "snap", handle } });

                    var checkoutDocument = new DxDocument(
                        new DxHeader("1.3", _sessionId, direction, null, null, null, null, false, null),
                        new[] { checkoutBlock });

                    var request = new DxExecutionRequest(
                        checkoutDocument,
                        $"%%FS op=\"checkout\" snap=\"{handle}\"",
                        direction,
                        mode,
                        progress,
                        onBaseMismatch,
                        runTimeoutSeconds,
                        ct,
                        CheckoutTargetHandle: handle);

                    result = await ExecuteCoreAsync(request);
                }
            }
            else
            {
                var (document, parseErrors) = DxParser.ParseText(rawText);
                var optionsOnBaseMismatch = onBaseMismatch;
                var optionsRunTimeoutSeconds = runTimeoutSeconds is > 0 ? runTimeoutSeconds : null;

                if (document is null)
                {
                    var diagnostics = parseErrors.Select(e => new DxDiagnostic(
                        "DX-PARSE-001", e.Message, DxDiagnosticSeverity.Error, $"Line: {e.LineNumber}")).ToArray();

                    result = new DxResult(
                        DxResultStatus.ValidationFailure,
                        "Document parse failed.",
                        null,
                        diagnostics,
                        isDryRun);
                }
                else
                {
                    var request = new DxExecutionRequest(
                        document, rawText, direction, mode, progress,
                        optionsOnBaseMismatch,
                        optionsRunTimeoutSeconds,
                        ct);

                    result = await ExecuteCoreAsync(request);
                }
            }
        }
        catch (Exception ex)
        {
            result = ex.AsExecutionFailure(isDryRun);
        }
        finally
        {
            await WriteResultLogAsync(direction, result);
        }

        return result;
    }

    private static string? ExtractCheckoutHandle(string rawText)
    {
        if (string.IsNullOrWhiteSpace(rawText)) return null;

        // Match "(checkout T0001)" or "snap=\"T0001\""
        var match = System.Text.RegularExpressions.Regex.Match(
            rawText,
            @"(?:checkout\s+([A-Z]\d{4})|snap=""([A-Z]\d{4})"")",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        return match.Success ? (match.Groups[1].Value ?? match.Groups[2].Value) : null;
    }

    // Internal helpers moved from DxRuntime to host tool operations under dispatcher authority.
    internal async Task<DxResult> ApplyAsync(
        string rawText,
        string direction,
        bool isDryRun = false,
        IProgress<string>? progress = null,
        string? onBaseMismatch = null,
        int? runTimeoutSeconds = null,
        CancellationToken ct = default)
    {
        // Reuse existing DispatchAsync entrypoint which owns parsing and logging.
        return await DispatchAsync(rawText, direction, isDryRun ? DxExecutionMode.DryRun : DxExecutionMode.Apply, progress, onBaseMismatch, runTimeoutSeconds, ct);
    }

    internal async Task<DxResult> ToolAsync(
            string op,
            string raw,
            string? arg,
            CancellationToken ct)
    {
        return await DispatchAsync(
            new DxExecutionRequest(
                DxDocument.Empty,
                raw,
                "tool",
                DxExecutionMode.Apply,
                null,
                null,
                null,
                ct,
                op,
                arg));
    }

    internal async Task<string> MaterializeSnapAsync(string handle, CancellationToken ct = default)
    {
        var result = await ToolAsync("materialize", $"(materialize {handle})", handle, ct);
        return result.Metadata!["materialize_dir"].ToString()!;
    }

    // Public wrappers for dispatcher-hosted tool operations intended for in-process CLI use.
    public Task<string> Materialize(string handle, CancellationToken ct = default)
        => MaterializeSnapAsync(handle, ct);

    /// <summary>
    /// Dispatches an execution request without logging.
    /// </summary>
    /// <param name="request">The execution request to dispatch.</param>
    /// <returns>A task representing the asynchronous operation, containing the execution result.</returns>
    /// <remarks>Internal method for in-process callers that already have a document. Does not log; assumes
    /// the caller has already logged or is a trusted system path.</remarks>
    internal async Task<DxResult> DispatchAsync(DxExecutionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        DxResult result = new(DxResultStatus.ExecutionFailure, "Unexpected dispatcher fault.", null, null, request.IsDryRun);

        try
        {
            result = await ExecuteCoreAsync(request);
        }
        catch (Exception ex)
        {
            result = ex.AsExecutionFailure(request.IsDryRun);
        }
        finally
        {
            await WriteResultLogAsync(request.Direction, result);
        }

        return result;
    }

    /// <summary>
    /// Executes the core logic of a pre-parsed request without touching the session log.
    /// Logging is the responsibility of the calling <c>DispatchAsync</c> overload.
    /// </summary>
    private async Task<DxResult> ExecuteCoreAsync(DxExecutionRequest request)
    {
        var (document, _, _, _, progress, onBaseMismatch, runTimeoutSeconds, ct, _, _, checkoutTargetHandle) = request;
        var isDryRun = request.IsDryRun;

        DxResult result;

        try
        {
            // Route to read-only path when the document has no mutating blocks, OR when
            // the caller explicitly requested read-only execution (Mode=Request).
            // Mode=Request MUST NOT mutate files, create snapshots, or write session_state.
            if (!document.IsMutating || request.IsReadOnlyMode)
            {
                _logger.Debug(request.IsReadOnlyMode
                    ? "Executing in Request (read-only) mode — mutation blocks skipped."
                    : "Document is read-only.");
                var roOps = new List<OperationResult>();
                var roCtx = new BlockExecutionContext(
                    _workspaceRoot,
                    _sessionId,
                    _ignoreSet,
                    request.IsDryRun,
                    _connection,
                    _logger,
                    request.RunTimeoutSeconds,
                    null,
                    null,
                    request.CancellationToken);

                foreach (var block in document.Blocks)
                {
                    await DispatchReadOnlyBlockAsync(block, roOps, roCtx, ct);
                }

                result = ((string?)null).AsSuccess();
            }
            else
            {
                var dispatchResult = await ExecuteMutatingTransactionAsync(document, isDryRun, progress, onBaseMismatch, runTimeoutSeconds, ct);

                // Map legacy DispatchResult to canonical DxResult inline to avoid using the temporary Obsolete bridge
                var status = dispatchResult.Success ? DxResultStatus.Success
                           : dispatchResult.IsBaseMismatch ? DxResultStatus.BaseMismatch
                           : DxResultStatus.ExecutionFailure;

                var diagnostics = new List<DxDiagnostic>();
                if (!dispatchResult.Success)
                {
                    diagnostics.Add(new DxDiagnostic(
                        code: dispatchResult.IsBaseMismatch ? "BASE_MISMATCH" : "EXECUTION_ERROR",
                        message: dispatchResult.Error ?? "An unknown execution error occurred.",
                        severity: DxDiagnosticSeverity.Error
                    ));
                }

                result = new DxResult(
                    Status: status,
                    Message: dispatchResult.Error,
                    SnapId: dispatchResult.NewHandle,
                    Diagnostics: diagnostics,
                    IsDryRun: isDryRun,
                    Metadata: null,
                    Blocks: dispatchResult.Operations);
            }
        }
        catch (Exception ex)
        {
            result = ex.AsExecutionFailure(isDryRun);
        }

        return result;
    }

    /// <summary>
    /// Writes the result log entry. Called after every execution path to satisfy the dual-log invariant.
    /// </summary>
    /// <remarks>
    /// Uses <see cref="CancellationToken.None"/> unconditionally so that a cancelled caller token
    /// cannot suppress the result log entry — the dual-log invariant must hold on all exit paths.
    /// </remarks>
    private async Task WriteResultLogAsync(string direction, DxResult result)
    {
        await _store.WriteSessionLogAsync(new SessionLogEntry
        {
            SessionId = _sessionId,
            Direction = direction,
            Document = DxResultLoggingSerializer.Serialize(result),
            TxSuccess = result.IsSuccess ? 1 : 0,
            SnapHandle = result.SnapId
        }, CancellationToken.None);
    }

    /// <summary>
    /// Executes a mutating transaction on the specified document, applying all mutation and run blocks, and commits the
    /// resulting state to the workspace.
    /// </summary>
    /// <remarks>This method acquires a workspace lock to ensure exclusive access during the transaction. If
    /// the document's base does not match the current workspace state, the transaction may be rejected or a warning
    /// issued, depending on the provided options. Failure logs are recorded even if the transaction is aborted,
    /// ensuring audit continuity.</remarks>
    /// <param name="document">The document containing mutation and run blocks to be applied as part of the transaction.</param>
    /// <param name="dryRun">If <see langword="true"/>, simulates the transaction without persisting any changes or creating a log entry;
    /// otherwise, applies and commits the mutations.</param>
    /// <param name="progress">An optional progress reporter that receives status messages as the transaction progresses. May be <see
    /// langword="null"/>.</param>
    /// <param name="options">Optional settings that control transaction behavior, such as base mismatch handling and run block timeouts. May
    /// be <see langword="null"/>.</param>
    /// <param name="ct">A cancellation token that can be used to cancel the transaction operation.</param>
    /// <returns>A <see cref="DispatchResult"/> indicating the outcome of the transaction, including success status, resulting
    /// handle, error messages, and a list of operation results.</returns>
    /// <exception cref="DxException">Thrown if a run block fails during the commit-gate phase or if an invalid argument is encountered in a run
    /// block.</exception>
    private async Task<DispatchResult> ExecuteMutatingTransactionAsync(
                DxDocument document,
                bool dryRun,
                IProgress<string>? progress,
                string? onBaseMismatch,
                int? runTimeoutSeconds,
                CancellationToken ct)
    {
        var lockFile = Path.Combine(_workspaceRoot, ".dx", "snaps.lock");
        await using var dxLock = await DxLock.AcquireAsync(lockFile, TimeSpan.FromSeconds(5), ct);

        var coordinator = new TransactionCoordinator(_connection, _workspaceRoot, _ignoreSet, _logger);
        var operations = new List<OperationResult>();

        return await coordinator.RunAsync(_sessionId, async (tx, innerCt) =>
        {
            var currentHead = await GetCurrentHeadAsync(innerCt);

            if (document.Header.Base is { } baseHandle)
            {
                var baseHash = HandleAssigner.Resolve(_connection, _sessionId, baseHandle);
                if (baseHash == null || !DxHash.Equal(baseHash, currentHead))
                {
                    var actual = HandleAssigner.ReverseResolve(_connection, _sessionId, currentHead) ?? "?";
                    var message = $"Base mismatch. Expected: {baseHandle}, Actual: {actual}";

                    if ((onBaseMismatch ?? string.Empty).ToLowerInvariant() != "warn")
                    {
                        return new DispatchResult(false, null, message, operations, IsBaseMismatch: true);
                    }

                    _logger.Warn(message);
                }
            }

            if (dryRun) return new DispatchResult(true, null, null, operations);

            var blockCtx = new BlockExecutionContext(_workspaceRoot, _sessionId, _ignoreSet, dryRun, _connection, _logger, runTimeoutSeconds, this, tx, innerCt);

            foreach (var block in document.Blocks.Where(IsMutating))
            {
                innerCt.ThrowIfCancellationRequested();
                await DispatchMutationBlockAsync(block, operations, blockCtx, innerCt);
            }

            foreach (var run in document.Blocks.OfType<RequestBlock>().Where(r => r.Type == "run"))
            {
                innerCt.ThrowIfCancellationRequested();
                var opResult = await _executors[BlockType.Request].ExecuteAsync(run, blockCtx, innerCt);
                operations.Add(opResult);
                if (!opResult.Success) throw new DxException(DxError.InvalidArgument, $"Gate failed: {opResult.Detail}");
            }

            // Normal document commit: build manifest and persist snapshot
            var manifest = ManifestBuilder.Build(_workspaceRoot, _ignoreSet);
            var snapHash = ManifestBuilder.ComputeSnapHash(manifest);

            string newHandle;
            bool isCheckout = document.Blocks.Any(b => b is FsBlock fs && fs.Op == "checkout");

            if (DxHash.Equal(snapHash, currentHead) && !isCheckout)
            {
                newHandle = HandleAssigner.ReverseResolve(_connection, _sessionId, currentHead)!;
            }
            else
            {
                var writer = new SnapshotWriter(_connection);
                newHandle = await writer.PersistAsync(_sessionId, snapHash, manifest, innerCt, tx);
            }

            _logger.Info($"→ {newHandle}");
            return new DispatchResult(true, newHandle, null, operations);
        }, ct);
    }

    /// <summary>
    /// Performs the checkout core algorithm using the provided active transaction. All file writes
    /// and database operations must use the supplied transaction.
    /// </summary>
    public async Task<DxResult> ExecuteCheckoutCoreAsync(
        string targetHandle,
        SqliteTransaction tx,
        IProgress<string>? progress,
        CancellationToken ct)
    {
        try
        {
            // 1. Resolve target
            var targetHash = HandleAssigner.Resolve(_connection, _sessionId, targetHandle)
                ?? throw new DxException(DxError.SnapNotFound, $"Snap not found: {targetHandle}");

            // 2. Get current head for parent link (sync methods — no Async version exists)
            var currentHeadHash = await GetCurrentHeadAsync(ct);
            var currentHeadHandle = HandleAssigner.ReverseResolve(_connection, _sessionId, currentHeadHash);

            // 3. Restore files
            var engine = new RollbackEngine(_connection, _workspaceRoot, _ignoreSet);
            await Task.Run(() => engine.RestoreTo(targetHash), ct);
            progress?.Report("checkout: restored");

            // 4. Build manifest
            var manifest = ManifestBuilder.Build(_workspaceRoot, _ignoreSet);
            var newHash = ManifestBuilder.ComputeSnapHash(manifest);

            string resultHandle;
            // 5. Idempotency check — reuse current handle if nothing changed
            if (DxHash.Equal(newHash, targetHash) && DxHash.Equal(newHash, currentHeadHash))
            {
                resultHandle = currentHeadHandle!;
            }
            else
            {
                // 6. Persist new snap inside the supplied transaction
                var writer = new SnapshotWriter(_connection);
                resultHandle = await writer.PersistAsync(_sessionId, newHash, manifest, ct, tx);

                // 7. Set parent and checkout_of (required by spec)
                await _connection.ExecuteAsync(
                    @"UPDATE snaps 
                  SET parent_hash = @parent, checkout_of = @checkoutOf 
                  WHERE snap_hash = @hash",
                    new { parent = currentHeadHash, checkoutOf = targetHash, hash = newHash },
                    transaction: tx);
            }

            return new DxResult(DxResultStatus.Success, null, resultHandle, Array.Empty<DxDiagnostic>(), false);
        }
        catch (DxException dx)
        {
            return dx.AsExecutionFailure(false);
        }
        catch (OperationCanceledException)
        {
            return new DxResult(DxResultStatus.Canceled, "Checkout cancelled", null, null, false);
        }
        catch (Exception ex)
        {
            return ex.AsExecutionFailure(false);
        }
    }

    private async Task DispatchMutationBlockAsync(DxBlock block, List<OperationResult> ops, BlockExecutionContext ctx, CancellationToken ct)
    {
        if (!_executors.TryGetValue(GetBlockType(block), out var executor))
            return;

        var result = await executor.ExecuteAsync(block, ctx, ct);
        ops.Add(result);

        if (!result.Success)
            throw new DxException(DxError.InvalidArgument, result.Detail ?? "Block execution failed.");
    }

    private async Task DispatchReadOnlyBlockAsync(DxBlock block, List<OperationResult> ops, BlockExecutionContext ctx, CancellationToken ct)
    {
        if (block is not RequestBlock) return;

        var result = await _executors[BlockType.Request].ExecuteAsync(block, ctx, ct);
        ops.Add(result);
    }

    private async Task<byte[]> GetCurrentHeadAsync(CancellationToken ct)
    {
        return await _connection.ExecuteScalarAsync<byte[]>(new CommandDefinition(
            "SELECT head_snap_hash FROM session_state WHERE session_id = @sid",
            new { sid = _sessionId }, cancellationToken: ct))
            ?? throw new DxException(DxError.SessionNotFound, $"Session has no HEAD: {_sessionId}");
    }

    private static bool IsMutating(DxBlock block) => block.IsMutating;
}
