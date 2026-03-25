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
);




