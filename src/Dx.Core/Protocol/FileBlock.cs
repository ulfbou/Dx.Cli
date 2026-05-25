namespace Dx.Core.Protocol;

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
) : DxBlock
{
    /// <summary>
    /// Deconstructs the file block into its component properties, allowing for deconstruction assignment syntax
    ///  (e.g. <c>var (path, encoding, readOnly, lines, create, ifContains, ifLine, content) = fileBlock;</c>).
    /// </summary>
    /// <param name="path">When this method returns, contains the file path.</param>
    /// <param name="encoding">When this method returns, contains the encoding string.</param>
    /// <param name="readOnly">When this method returns, contains the value indicating whether the block is read-only.</param>
    /// <param name="lines">When this method returns, contains the line range specification or null.</param>
    /// <param name="create">When this method returns, contains the value indicating whether to create the file if it does not exist.</param>
    /// <param name="ifContains">When this method returns, contains the precondition string for content containment or null.</param>
    /// <param name="ifLine">When this method returns, contains the precondition string for line pattern matching or null.</param>
    /// <param name="content">When this method returns, contains the content to write to the file.</param>
    public void Deconstruct(
        out string path,
        out string encoding,
        out bool readOnly,
        out string? lines,
        out bool create,
        out string? ifContains,
        out string? ifLine,
        out string content)
    {
        path = Path;
        encoding = Encoding;
        readOnly = ReadOnly;
        lines = Lines;
        create = Create;
        ifContains = IfContains;
        ifLine = IfLine;
        content = Content;
    }
}
