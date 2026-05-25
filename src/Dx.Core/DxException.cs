namespace Dx.Core;

/// <summary>
/// Represents a DX-domain error, coupling an error code with a human-readable message.
/// </summary>
public sealed class DxException(DxError error, string message)
    : Exception(message)
{
    /// <summary>Gets the specific <see cref="DxError"/> code for this exception.</summary>
    public DxError Error { get; } = error;

    /// <summary>
    /// Returns <see langword="true"/> when the given error code indicates a recoverable
    /// condition — one in which the workspace remains consistent and no rollback is needed.
    /// </summary>
    public static bool IsRecoverable(DxError e) => e switch
    {
        DxError.BaseMismatch => true,
        DxError.RunGateFailed => true,
        DxError.SnapNotFound => true,
        DxError.NoOp => true,
        DxError.PathEscapesRoot => true,
        DxError.InvalidArgument => true,
        DxError.WorkspaceNotInitialized => true,
        DxError.ParseError => true,
        _ => false,
    };

    /// <summary>
    /// Maps a <see cref="DxError"/> value to a conventional process exit code.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Exit codes emitted by DX are stable, user‑visible contracts intended for
    /// automation, scripting, and CI environments.
    /// </para>
    /// <para>
    /// The following conventions are enforced:
    /// </para>
    /// <list type="bullet">
    ///   <item>
    ///     <description><c>1</c> — Run‑gate failures or fatal execution errors.</description>
    ///   </item>
    ///   <item>
    ///     <description><c>2</c> — Validation or parse failures detected before execution.</description>
    ///   </item>
    ///   <item>
    ///     <description><c>3</c> — Base‑mismatch conflicts that prevent safe execution.</description>
    ///   </item>
    /// </list>
    /// <para>
    /// New <see cref="DxError"/> values MUST map to an existing category or
    /// explicitly document a new exit code.
    /// </para>
    /// </remarks>
    /// <returns>
    /// A non‑zero integer representing the process exit code associated with
    /// the supplied <see cref="DxError"/>.
    /// </returns>
    public static int ExitCode(DxError e) => e switch
    {
        DxError.BaseMismatch => 3,
        DxError.NoOp => 0,
        DxError.RunGateFailed => 1, // Gates are standard execution failures
        DxError.SnapNotFound => 2,
        DxError.PathEscapesRoot => 2,
        DxError.InvalidArgument => 2,
        DxError.WorkspaceNotInitialized => 2,
        DxError.ParseError => 2,
        DxError.SessionNotFound => 1,
        DxError.PendingTransactionOnOtherSession => 1,
        DxError.DatabaseCorruption => 1,
        DxError.VerificationFailed => 1,
        _ => 1,
    };
}
