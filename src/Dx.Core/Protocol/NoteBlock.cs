namespace Dx.Core.Protocol;

/// <summary>
/// Represents a <c>%%NOTE</c> block containing human-readable commentary or a directory
/// tree that is included for context but ignored by the dispatcher.
/// </summary>
/// <param name="Content">The indentation-stripped body of the note.</param>
public sealed record NoteBlock(string Content) : DxBlock
{
    /// <summary>
    /// Deconstructs the note block into its content property, allowing for deconstruction assignment syntax
    ///  (e.g. <c>var content = noteBlock;</c>).
    /// </summary>
    /// <param name="content">When this method returns, contains the content of the note.</param>
    public void Deconstruct(out string content)
    {
        content = Content;
    }
}
