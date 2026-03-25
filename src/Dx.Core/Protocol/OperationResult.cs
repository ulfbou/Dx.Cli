namespace Dx.Core.Protocol;

/// <summary>
/// Represents the outcome of a single block operation within a dispatched transaction.
/// </summary>
/// <param name="BlockType">
/// The block type string (e.g. <c>FILE</c>, <c>PATCH</c>, <c>FS:move</c>,
/// <c>REQUEST:run</c>).
/// </param>
/// <param name="Path">The workspace-relative path affected by the operation, if applicable.</param>
/// <param name="Success"><see langword="true"/> when the operation completed without error.</param>
/// <param name="Detail">An optional human-readable detail string (e.g. hunk count, exit code).</param>
public sealed record OperationResult(
    string BlockType,
    string? Path,
    bool Success,
    string? Detail
);




