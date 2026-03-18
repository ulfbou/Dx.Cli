namespace Dx.Core;

public enum DxError
{
    // Recoverable
    BaseMismatch,
    SnapNotFound,
    NoOp,
    PathEscapesRoot,
    InvalidArgument,
    WorkspaceNotInitialized,
    SessionNotFound,
    ParseError,

    // Fatal
    PendingTransactionOnOtherSession,
    DatabaseCorruption,
    VerificationFailed,
    WorkspaceAlreadyInitialized,
}

public sealed class DxException(DxError error, string message)
    : Exception(message)
{
    public DxError Error { get; } = error;

    public static bool IsRecoverable(DxError e) => e switch
    {
        DxError.BaseMismatch => true,
        DxError.SnapNotFound => true,
        DxError.NoOp => true,
        DxError.PathEscapesRoot => true,
        DxError.InvalidArgument => true,
        DxError.WorkspaceNotInitialized => true,
        DxError.ParseError => true,
        _ => false,
    };

    public static int ExitCode(DxError e) => e switch
    {
        DxError.BaseMismatch => 3,
        DxError.NoOp => 0,
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
