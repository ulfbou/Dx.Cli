namespace Dx.Core.Protocol;

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
/// <param name="Title">An optional freeform title for the transaction, used in session logs.</param>
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
    string? Title,
    string? Base,
    string? Root,
    string? Target,
    bool    ReadOnly,
    string? ArtifactsDir)
{
    /// <summary>
    /// Deconstructs the header into its component properties, allowing for deconstruction assignment syntax
    ///  (e.g. <c>var (version, session, author, title, base, root, target, readOnly, artifactsDir) = header;</c>).
    /// </summary>
    /// <param name="version">When this method returns, contains the version string.</param>
    /// <param name="session">When this method returns, contains the session identifier or null.</param>
    /// <param name="author">When this method returns, contains the author identifier or null.</param>
    /// <param name="title">When this method returns, contains the title or null.</param>
    /// <param name="base">When this method returns, contains the base snapshot handle or null.</param>
    /// <param name="root">When this method returns, contains the root path override or null.</param>
    /// <param name="target">When this method returns, contains the target path specifier or null.</param>
    /// <param name="readOnly">When this method returns, contains the value indicating whether the document is read-only.</param>
    /// <param name="artifactsDir">When this method returns, contains the artifacts directory path or null.</param>
    public void Deconstruct(
        out string version,
        out string? session,
        out string? author,
        out string? title,
        out string? @base,
        out string? root,
        out string? target,
        out bool readOnly,
        out string? artifactsDir)
    {
        version = Version;
        session = Session;
        author = Author;
        title = Title;
        @base = Base;
        root = Root;
        target = Target;
        readOnly = ReadOnly;
        artifactsDir = ArtifactsDir;
    }
}
