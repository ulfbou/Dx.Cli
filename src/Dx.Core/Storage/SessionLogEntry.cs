namespace Dx.Core.Storage;

/// <summary>
/// Represents a discrete entry in the authoritative <c>session_log</c>.
/// </summary>
public sealed record SessionLogEntry
{
    /// <summary>
    /// Gets the unique identifier for the session.
    /// </summary>
    public required string SessionId { get; init; }

    /// <summary>
    /// Gets or sets the actor responsible for the document (e.g., "llm", "tool").
    /// </summary>
    public required string Direction { get; init; }

    /// <summary>
    /// Gets or sets the byte-accurate raw document content or serialized result.
    /// </summary>
    public required string Document { get; init; }

    /// <summary>
    /// Gets or sets the transaction success indicator. 
    /// Use <see langword="null"/> for input logs, <c>1</c> for success, or <c>0</c> for failure.
    /// </summary>
    public int? TxSuccess { get; init; }

    /// <summary>
    /// Gets or sets the resulting snapshot handle if the execution modified state.
    /// </summary>
    public string? SnapHandle { get; init; }
}
