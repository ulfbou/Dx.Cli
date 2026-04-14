namespace Dx.Core.Execution.Results;

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
public sealed partial class DxResult
{
    /// <summary>
    /// Gets the high-level execution status.
    /// </summary>
    public DxResultStatus Status { get; }

    /// <summary>
    /// Gets an optional summary message describing the execution outcome.
    /// </summary>
    /// <value>
    /// May be <see langword="null"/> when the status alone is sufficient
    /// to describe the outcome.
    /// </value>
    public string? Message { get; }

    /// <summary>
    /// Gets the snapshot identifier produced or evaluated during execution.
    /// </summary>
    /// <value>
    /// Returns <see langword="null"/> when execution did not produce
    /// or evaluate a snapshot.
    /// </value>
    public string? SnapId { get; }

    /// <summary>
    /// Gets the collection of diagnostics generated during execution.
    /// </summary>
    /// <value>
    /// Guaranteed to be non-null. May be empty.
    /// </value>
    public IReadOnlyList<DxDiagnostic> Diagnostics { get; }

    /// <summary>
    /// Gets a value indicating whether the execution was evaluated
    /// in dry-run mode.
    /// </summary>
    public bool IsDryRun { get; }

    /// <summary>
    /// Gets optional structured metadata associated with the result.
    /// </summary>
    /// <remarks>
    /// Intended for advanced consumers and tooling integrations.
    /// Consumers should not rely on undocumented keys.
    /// </remarks>
    public IReadOnlyDictionary<string, object>? Metadata { get; }

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
    }

    /// <summary>
    /// Deconstructs the result into its component properties, allowing for deconstruction assignment syntax
    /// (e.g. <c>var (status, message, snapId, diagnostics, isDryRun, metadata) = result;</c>).
    /// </summary>
    /// <param name="status">When this method returns, contains the status of the result.</param>
    /// <param name="message">When this method returns, contains the message associated with the result, or null if no message is present.</param>
    /// <param name="snapId">When this method returns, contains the snapshot identifier, or null if not applicable.</param>
    /// <param name="diagnostics">When this method returns, contains a read-only list of diagnostics associated with the result.</param>
    /// <param name="isDryRun">When this method returns, indicates whether the operation was a dry run.</param>
    /// <param name="metadata">When this method returns, contains a read-only dictionary of metadata associated with the result, or null if no
    /// metadata is present.</param>
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
