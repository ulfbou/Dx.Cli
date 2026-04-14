namespace Dx.Core.Protocol;

/// <summary>
/// Represents the overall outcome of a <see cref="DxDispatcher.DispatchAsync"/> call.
/// </summary>
/// <param name="Success"><see langword="true"/> when the transaction committed successfully.</param>
/// <param name="NewHandle">
/// The handle of the snapshot produced by the transaction (e.g. <c>T0004</c>), or
/// <see langword="null"/> when no snapshot was created (failure or no-op).
/// </param>
/// <param name="Error">
/// A human-readable error message when <paramref name="Success"/> is
/// <see langword="false"/>.
/// </param>
/// <param name="Operations">The per-block operation results recorded during dispatch.</param>
/// <param name="IsBaseMismatch">
/// <see langword="true"/> when the failure was specifically a base-handle mismatch,
/// allowing callers to return exit code <c>3</c> rather than <c>1</c>.
/// </param>
public sealed record DispatchResult(
    bool Success,
    string? NewHandle,
    string? Error,
    IReadOnlyList<OperationResult> Operations,
    bool IsBaseMismatch = false
)
{
    /// <summary>
    /// Deconstructs the result into its success status, new handle value, and error message.
    /// </summary>
    /// <param name="success">When this method returns, contains <see langword="true"/> if the operation was 
    /// successful; otherwise, <see langword="false"/>.</param>
    /// <param name="newHandle">When this method returns, contains the new handle value if the operation succeeded; 
    /// otherwise, <see langword="null"/>.</param>
    /// <param name="error">When this method returns, contains the error message if the operation failed; otherwise, 
    /// <see langword="null"/>.</param>
    public void Deconstruct(out bool success, out string? newHandle, out string? error)
    {
        success = Success;
        newHandle = NewHandle;
        error = Error;
    }

    /// <summary>
    /// Creates a new DispatchResult instance representing a successful dispatch operation.
    /// </summary>
    /// <param name="newHandle">The handle associated with the successful dispatch operation, or <see langword="null"/> 
    /// if no handle is generated.</param>
    /// <param name="operations">A read-only list of OperationResult objects representing the results of the operations 
    /// performed during the dispatch.</param>
    /// <returns>A DispatchResult instance indicating success, containing the specified handle and operation results.</returns>
    public static DispatchResult FromSuccess(string? newHandle, IReadOnlyList<OperationResult> operations) 
        => new DispatchResult(true, newHandle, null, operations);

    /// <summary>
    /// Creates a new instance of the DispatchResult class representing a failed dispatch operation.
    /// </summary>
    /// <param name="error">A string describing the error that caused the dispatch to fail. Cannot be 
    /// <see langword="=""null"/>.</param>
    /// <param name="operations">A read-only list of OperationResult objects representing the results of attempted operations. 
    /// Cannot be <see langword="null"/>.</param>
    /// <param name="isBaseMismatch"><see langword="true"/> if the failure was due to a base mismatch; otherwise, 
    /// <see langword="false"/>. This flag allows callers to distinguish base mismatch failures for specialized handling (e.g., 
    /// returning a specific exit code).</param>
    /// <returns>A DispatchResult instance indicating failure, containing the specified error information and operation 
    /// results.</returns>
    public static DispatchResult FromFailure(string error, IReadOnlyList<OperationResult> operations, bool isBaseMismatch = false) 
        => new DispatchResult(false, null, error, operations, isBaseMismatch);
}
