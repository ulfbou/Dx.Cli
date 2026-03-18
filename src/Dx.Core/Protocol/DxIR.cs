namespace Dx.Core.Protocol;

// ── Header ──────────────────────────────────────────────────────────────────

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

// ── Blocks ───────────────────────────────────────────────────────────────────

public abstract record DxBlock;

public sealed record FileBlock(
    string  Path,
    string  Encoding,
    bool    ReadOnly,
    string? Lines,
    bool    Create,
    string? IfContains,
    string? IfLine,
    string  Content        // indentation-stripped body text
) : DxBlock;

public sealed record PatchHunk(
    string  Operation,     // replace | insert | delete
    string  Target,        // lines=N-M | pattern="..." | after-line=N | etc.
    bool    All,           // for replace/delete pattern all="true"
    string  Body           // replacement / insertion content (stripped)
);

public sealed record PatchBlock(
    string  Path,
    IReadOnlyList<PatchHunk> Hunks
) : DxBlock;

public sealed record FsBlock(
    string  Op,            // move | delete | encode | checkout | restore
    IReadOnlyDictionary<string, string> Args
) : DxBlock;

public sealed record RequestBlock(
    string  Type,          // file | tree | search | metadata | git-status | diff | snaps | run
    IReadOnlyDictionary<string, string> Args,
    string  Body           // command body for type=run
) : DxBlock;

public sealed record ResultBlock(
    string  For,
    string  Status,        // ok | error | partial
    IReadOnlyDictionary<string, string> Args,
    string  Body,
    SnapBlock? Snap        // nested snap, if present
) : DxBlock;

public sealed record SnapBlock(
    string  Id,
    string  Parent,
    string? CheckoutOf
) : DxBlock;

public sealed record NoteBlock(string Content) : DxBlock;

// ── Document ─────────────────────────────────────────────────────────────────

public sealed record DxDocument(
    DxHeader Header,
    IReadOnlyList<DxBlock> Blocks
)
{
    public bool IsMutating => Blocks.Any(b =>
        b is FileBlock f && !f.ReadOnly ||
        b is PatchBlock ||
        b is FsBlock fs && fs.Op is "move" or "delete" or "encode" or "restore" or "checkout");
}

// ── Parse errors ─────────────────────────────────────────────────────────────

public sealed record ParseError(int Line, string Message);
