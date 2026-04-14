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
)
{
    /// <summary>
    /// Deconstructs the object into its component values.
    /// </summary>
    /// <remarks>Use this method to deconstruct the object into individual variables for easier access to its
    /// properties.</remarks>
    /// <param name="blockType">When this method returns, contains the block type represented by the object.</param>
    /// <param name="path">When this method returns, contains the path associated with the object, or null if no path is set.</param>
    /// <param name="success">When this method returns, contains a value indicating whether the operation was successful.</param>
    /// <param name="detail">When this method returns, contains additional detail information, or null if no detail is available.</param>
    public void Deconstruct(out string blockType, out string? path, out bool success, out string? detail)
    {
        blockType = BlockType;
        path = Path;
        success = Success;
        detail = Detail;
    }

    public static OperationResult Create(string blockType, string? path = null, bool success = true, string? detail = null)
        => new(blockType, path, success, detail);

    /// <summary>
    /// Creates a successful OperationResult for the specified block type.
    /// </summary>
    /// <param name="blockType">The type of block associated with the operation result. Cannot be null.</param>
    /// <param name="path">The optional path related to the block. May be null if not applicable.</param>
    /// <param name="detail">An optional detail message providing additional information about the operation. May be null.</param>
    /// <returns>A successful OperationResult instance representing the specified block type and optional details.</returns>
    public static OperationResult SuccessResult(string blockType, string? path = null, string? detail = null)
        => new(blockType, path, true, detail);
    
    /// <summary>
    /// Creates a failed operation result for the specified block type.
    /// </summary>
    /// <param name="blockType">The type of block associated with the failed operation. Cannot be null.</param>
    /// <param name="path">The optional path that identifies the location related to the failure. May be null if not applicable.</param>
    /// <param name="detail">An optional detail message describing the reason for the failure. May be null if no additional detail is provided.</param>
    /// <returns>An OperationResult instance representing a failed operation for the specified block type.</returns>
    public static OperationResult FailureResult(string blockType, string? path = null, string? detail = null)
        => new(blockType, path, false, detail);
}
