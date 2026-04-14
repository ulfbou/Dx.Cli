namespace Dx.Core.Execution.Results;

using Dx.Core.Protocol;

/// <summary>
/// Represents the complete, immutable outcome of a Dx execution.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="DxResult"/> is the canonical execution contract between
/// the engine, CLI, and automation layers.
/// </para>
/// <para>
/// Instances must be treated as terminal and stable once created.
/// Consumers may safely cache, serialize, or evaluate the result
/// without concern for mutation.
/// </para>
/// </remarks>
public sealed partial record DxResult
{
    /// <summary>
    /// Gets the high-level execution status.
    /// </summary>
    public DxResultStatus Status { get; init; }

    /// <summary>
    /// Gets an optional summary message describing the execution outcome.
    /// </summary>
    /// <value>
    /// May be <see langword="null"/> when the status alone is sufficient
    /// to describe the outcome.
    /// </value>
    public string? Message { get; init; }

    /// <summary>
    /// Gets the snapshot identifier produced or evaluated during execution.
    /// </summary>
    /// <value>
    /// Returns <see langword="null"/> when execution did not produce
    /// or evaluate a snapshot.
    /// </value>
    public string? SnapId { get; init; }

    /// <summary>
    /// Gets the collection of diagnostics generated during execution.
    /// </summary>
    /// <value>
    /// Guaranteed to be non-null. May be empty.
    /// </value>
    public IReadOnlyList<DxDiagnostic> Diagnostics { get; init; }

    /// <summary>
    /// Gets a value indicating whether the execution was evaluated
    /// in dry-run mode.
    /// </summary>
    public bool IsDryRun { get; init; }

    /// <summary>
    /// Gets optional structured metadata associated with the result.
    /// </summary>
    /// <remarks>
    /// Intended for advanced consumers and tooling integrations.
    /// Consumers should not rely on undocumented keys.
    /// </remarks>
    public IReadOnlyDictionary<string, object>? Metadata { get; init; }

    /// <summary>
    /// Gets the list of individual block execution outcomes.
    /// </summary>
    /// <remarks>
    /// This property provides the granular audit trail for each block in the source document.
    /// It is required for canonical result serialization and is guaranteed to be non-null.
    /// </remarks>
    public IReadOnlyList<OperationResult> Blocks { get; init; } = Array.Empty<OperationResult>();

    /// <summary>
    /// Gets a value indicating whether the result represents a successful operation.
    /// </summary>
    public bool IsSuccess => Status == DxResultStatus.Success;

    /// <summary>
    /// Gets a value indicating whether the execution resulted in a permanent state change
    /// being committed to the workspace.
    /// </summary>
    /// <remarks>
    /// This property is <see langword="true"/> only if the execution was successful 
    /// and was not a dry run.
    /// </remarks>
    public bool IsCommitted => IsSuccess && !IsDryRun;

    /// <summary>
    /// Gets a value indicating whether the execution represents failure.
    /// </summary>
    /// <remarks>
    /// This convenience property enables fast branching without
    /// enumerating individual status values.
    /// </remarks>
    public bool IsFailure =>
        Status is DxResultStatus.BaseMismatch
               or DxResultStatus.ValidationFailure
               or DxResultStatus.ExecutionFailure;

    /// <summary>
    /// Initializes a new instance of the <see cref="DxResult"/> class.
    /// </summary>
    /// <param name="status">The terminal execution status.</param>
    /// <param name="message">An optional summary message.</param>
    /// <param name="snapId">An optional snapshot identifier.</param>
    /// <param name="diagnostics">
    /// The collection of diagnostics produced during execution.
    /// If <see langword="null"/>, an empty collection is used.
    /// </param>
    /// <param name="isDryRun">Indicates whether execution was a dry run.</param>
    /// <param name="metadata">Optional structured metadata.</param>
    public DxResult(
            DxResultStatus status,
            string? message,
            string? snapId,
            IReadOnlyList<DxDiagnostic>? diagnostics,
            bool isDryRun,
            IReadOnlyDictionary<string, object>? metadata = null)
    {
        Status = status;
        Message = message;
        SnapId = snapId;
        Diagnostics = diagnostics ?? Array.Empty<DxDiagnostic>();
        IsDryRun = isDryRun;
        Metadata = metadata;
        Blocks = Array.Empty<OperationResult>(); // Explicit DX-First Initialization
    }

    /// <summary>
    /// Deconstructs the result into its component properties.
    /// </summary>
    /// <param name="status">The result status.</param>
    /// <param name="message">The result message.</param>
    /// <param name="snapId">The snapshot identifier.</param>
    /// <param name="diagnostics">The diagnostics list.</param>
    /// <param name="isDryRun">Whether it was a dry run.</param>
    /// <param name="metadata">The metadata dictionary.</param>
    public void Deconstruct(
        out DxResultStatus status,
        out string? message,
        out string? snapId,
        out IReadOnlyList<DxDiagnostic> diagnostics,
        out bool isDryRun,
        out IReadOnlyDictionary<string, object>? metadata)
    {
        status = Status;
        message = Message;
        snapId = SnapId;
        diagnostics = Diagnostics;
        isDryRun = IsDryRun;
        metadata = Metadata;
    }
}
