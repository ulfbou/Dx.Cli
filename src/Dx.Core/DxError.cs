namespace Dx.Core;

/// <summary>
/// Enumerates all domain-specific error conditions that the DX runtime can raise.
/// </summary>
/// <remarks>
/// Errors are classified as either recoverable (the workspace is still in a consistent
/// state and the user can retry or correct the input) or fatal (the workspace may be
/// inconsistent and manual intervention may be required).
/// </remarks>
public enum DxError
{
    // ── Recoverable ───────────────────────────────────────────────────────────

    /// <summary>
    /// The <c>base=</c> handle supplied in the DX document does not match the current
    /// HEAD snapshot. The transaction was rejected without modifying the working tree.
    /// </summary>
    BaseMismatch,

    /// <summary>
    /// A <see cref="Dx.Core.Protocol.RequestBlock"/> of type <c>run</c> returned a non-zero 
    /// exit code. The transaction was rolled back to ensure the workspace remains in a 
    /// passing state.
    /// </summary>
    RunGateFailed,

    /// <summary>
    /// The specified snapshot handle does not exist in the current session.
    /// </summary>
    SnapNotFound,

    /// <summary>
    /// The applied document produced no mutations; the working tree is unchanged.
    /// This is a normal, non-error outcome.
    /// </summary>
    NoOp,

    /// <summary>
    /// A path in the document or operation resolves outside the workspace root,
    /// which is a security violation. The operation was rejected.
    /// </summary>
    PathEscapesRoot,

    /// <summary>
    /// An argument or option value supplied by the user or the document is invalid.
    /// </summary>
    InvalidArgument,

    /// <summary>
    /// The target directory has not been initialised as a DX workspace.
    /// Run <c>dxs init</c> to create the <c>.dx/</c> directory and genesis snapshot.
    /// </summary>
    WorkspaceNotInitialized,

    /// <summary>
    /// The specified session does not exist or has been closed.
    /// </summary>
    SessionNotFound,

    /// <summary>
    /// The DX document could not be parsed; one or more parse errors were found.
    /// </summary>
    ParseError,

    // ── Fatal ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// A pending transaction record belonging to a different session was found in the
    /// workspace database. This indicates a crash or concurrent access scenario that
    /// requires manual inspection.
    /// </summary>
    PendingTransactionOnOtherSession,

    /// <summary>
    /// The workspace database is in an inconsistent state, for example a content hash
    /// referenced by a snapshot manifest has no corresponding blob entry.
    /// </summary>
    DatabaseCorruption,

    /// <summary>
    /// A SHA-256 hash verification check failed after restoring a file from the blob
    /// store, indicating storage corruption or a race condition.
    /// </summary>
    VerificationFailed,

    /// <summary>
    /// An attempt was made to initialise a workspace in a directory that already contains
    /// a <c>.dx/</c> folder, or whose ancestor does.
    /// </summary>
    WorkspaceAlreadyInitialized,
}
