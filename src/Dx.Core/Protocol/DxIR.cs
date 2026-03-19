namespace Dx.Core.Protocol;

// ── Header ────────────────────────────────────────────────────────────────────

/// <summary>
/// Represents the parsed header of a DX document, which appears on the first
/// non-blank line as <c>%%DX v&lt;version&gt; [key=value ...]</c>.
/// </summary>
/// <param name="Version">The protocol version string (e.g. <c>1.3</c>).</param>
/// <param name="Session">The optional session identifier the document targets.</param>
/// <param name="Author">
/// The optional author identifier (<c>llm</c> or <c>tool</c>), used when recording
/// the transaction in the session log.
/// </param>
/// <param name="Base">
/// The optional snapshot handle the document was authored against. When present,
/// the dispatcher verifies that the workspace HEAD matches this handle before applying
/// any mutations.
/// </param>
/// <param name="Root">An optional workspace root path override embedded in the document.</param>
/// <param name="Target">An optional target path specifier embedded in the document.</param>
/// <param name="ReadOnly">
/// When <see langword="true"/>, the document contains no mutations and the dispatcher
/// skips the transaction lifecycle entirely.
/// </param>
/// <param name="ArtifactsDir">An optional artifacts directory path embedded in the document.</param>
public sealed record DxHeader(
    string  Version,
    string? Session,
    string? Author,
    string? Base,
    string? Root,
    string? Target,
    bool    ReadOnly,
    string? ArtifactsDir
);

// ── Blocks ────────────────────────────────────────────────────────────────────

/// <summary>
/// Abstract base type for all block nodes in a parsed DX document's IR (Intermediate
/// Representation). Each concrete block type corresponds to a <c>%%TOKEN</c> delimiter.
/// </summary>
public abstract record DxBlock;

/// <summary>
/// Represents a <c>%%FILE</c> block that writes or declares a workspace file.
/// </summary>
/// <param name="Path">The workspace-relative path of the file.</param>
/// <param name="Encoding">
/// The target encoding for the file (e.g. <c>utf-8</c>, <c>utf-8-bom</c>, <c>utf-16-le</c>).
/// </param>
/// <param name="ReadOnly">
/// When <see langword="true"/>, the block is informational only and the file is not written.
/// </param>
/// <param name="Lines">
/// An optional line range specification (e.g. <c>10-20</c>) indicating which lines of the
/// file are represented in the block body.
/// </param>
/// <param name="Create">
/// When <see langword="true"/> (the default), the file is created if it does not already exist.
/// When <see langword="false"/>, the write is rejected if the file does not exist.
/// </param>
/// <param name="IfContains">
/// An optional precondition string. The write is rejected unless the existing file content
/// contains this exact string.
/// </param>
/// <param name="IfLine">
/// An optional precondition in the form <c>N:pattern</c>. The write is rejected unless line
/// <c>N</c> of the existing file contains <c>pattern</c>.
/// </param>
/// <param name="Content">The indentation-stripped body text to write to the file.</param>
public sealed record FileBlock(
    string  Path,
    string  Encoding,
    bool    ReadOnly,
    string? Lines,
    bool    Create,
    string? IfContains,
    string? IfLine,
    string  Content
) : DxBlock;

/// <summary>
/// Represents a single hunk within a <c>%%PATCH</c> block.
/// </summary>
/// <param name="Operation">
/// The hunk operation type: <c>replace</c>, <c>insert</c>, or <c>delete</c>.
/// </param>
/// <param name="Target">
/// The hunk target specification, which may be a line range (<c>lines=N-M</c>),
/// a pattern (<c>pattern="..."</c>), or a positional anchor (<c>after-line=N</c>, etc.).
/// </param>
/// <param name="All">
/// When <see langword="true"/>, a pattern-based replace or delete applies to all matching
/// lines rather than just the first.
/// </param>
/// <param name="Body">
/// The replacement or insertion content (indentation-stripped), used for <c>replace</c>
/// and <c>insert</c> operations. Empty for <c>delete</c>.
/// </param>
public sealed record PatchHunk(
    string Operation,
    string Target,
    bool   All,
    string Body
);

/// <summary>
/// Represents a <c>%%PATCH</c> block that applies one or more surgical hunks to an
/// existing workspace file.
/// </summary>
/// <param name="Path">The workspace-relative path of the file to patch.</param>
/// <param name="Hunks">The ordered list of hunks to apply sequentially.</param>
public sealed record PatchBlock(
    string Path,
    IReadOnlyList<PatchHunk> Hunks
) : DxBlock;

/// <summary>
/// Represents a <c>%%FS</c> block that performs a filesystem operation such as moving,
/// deleting, re-encoding, or restoring a file.
/// </summary>
/// <param name="Op">
/// The operation name: <c>move</c>, <c>delete</c>, <c>encode</c>, <c>checkout</c>,
/// or <c>restore</c>.
/// </param>
/// <param name="Args">
/// A dictionary of key-value arguments for the operation (e.g. <c>from</c>, <c>to</c>,
/// <c>path</c>, <c>snap</c>).
/// </param>
public sealed record FsBlock(
    string Op,
    IReadOnlyDictionary<string, string> Args
) : DxBlock;

/// <summary>
/// Represents a <c>%%REQUEST</c> block, which asks the tool to provide information or
/// to execute a command as a transaction gate.
/// </summary>
/// <param name="Type">
/// The request type: <c>file</c>, <c>tree</c>, <c>search</c>, <c>metadata</c>,
/// <c>git-status</c>, <c>diff</c>, <c>snaps</c>, or <c>run</c>.
/// </param>
/// <param name="Args">Additional key-value arguments for the request.</param>
/// <param name="Body">
/// The command body for <c>type=run</c> requests; empty for informational request types.
/// </param>
public sealed record RequestBlock(
    string Type,
    IReadOnlyDictionary<string, string> Args,
    string Body
) : DxBlock;

/// <summary>
/// Represents a <c>%%RESULT</c> block containing the tool's response to a preceding
/// <c>%%REQUEST</c>, optionally embedding a nested <c>%%SNAP</c> block.
/// </summary>
/// <param name="For">The request type or identifier this result responds to.</param>
/// <param name="Status">The outcome status: <c>ok</c>, <c>error</c>, or <c>partial</c>.</param>
/// <param name="Args">Additional key-value metadata for the result.</param>
/// <param name="Body">The indentation-stripped result body text.</param>
/// <param name="Snap">An optional nested <see cref="SnapBlock"/>, if present.</param>
public sealed record ResultBlock(
    string  For,
    string  Status,
    IReadOnlyDictionary<string, string> Args,
    string  Body,
    SnapBlock? Snap
) : DxBlock;

/// <summary>
/// Represents a <c>%%SNAP</c> block, which records snapshot lineage metadata either
/// standalone or nested inside a <c>%%RESULT</c>.
/// </summary>
/// <param name="Id">The handle of the snapshot being described (e.g. <c>T0003</c>).</param>
/// <param name="Parent">The handle of the parent snapshot.</param>
/// <param name="CheckoutOf">
/// The handle of the snapshot this was checked out from, when applicable.
/// </param>
public sealed record SnapBlock(
    string  Id,
    string  Parent,
    string? CheckoutOf
) : DxBlock;

/// <summary>
/// Represents a <c>%%NOTE</c> block containing human-readable commentary or a directory
/// tree that is included for context but ignored by the dispatcher.
/// </summary>
/// <param name="Content">The indentation-stripped body of the note.</param>
public sealed record NoteBlock(string Content) : DxBlock;

// ── Document ──────────────────────────────────────────────────────────────────

/// <summary>
/// Represents a fully parsed DX document, consisting of a header and an ordered sequence
/// of typed blocks.
/// </summary>
/// <param name="Header">The parsed document header.</param>
/// <param name="Blocks">The ordered list of parsed blocks.</param>
public sealed record DxDocument(
    DxHeader Header,
    IReadOnlyList<DxBlock> Blocks
)
{
    /// <summary>
    /// Gets a value indicating whether the document contains any mutation blocks —
    /// that is, any non-readonly <see cref="FileBlock"/>, any <see cref="PatchBlock"/>,
    /// or any <see cref="FsBlock"/> with a mutating operation.
    /// </summary>
    /// <remarks>
    /// When <see langword="false"/>, the dispatcher skips the full transaction lifecycle
    /// and only evaluates <see cref="RequestBlock"/> entries.
    /// </remarks>
    /// <returns>
    /// <see langword="true"/> when the document contains at least one mutation block;
    /// otherwise <see langword="false"/>.
    /// </returns>
    public bool IsMutating => Blocks.Any(b =>
        b is FileBlock f && !f.ReadOnly ||
        b is PatchBlock ||
        b is FsBlock fs && fs.Op is "move" or "delete" or "encode" or "restore" or "checkout");
}

// ── Parse errors ──────────────────────────────────────────────────────────────

/// <summary>
/// Represents a single parse error encountered by <see cref="DxParser"/>, identifying
/// the source line and a human-readable description of the problem.
/// </summary>
/// <param name="Line">The 1-based line number in the source document where the error occurred.</param>
/// <param name="Message">A human-readable description of the parse error.</param>
public sealed record ParseError(int Line, string Message);
