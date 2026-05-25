namespace Dx.Core;

/// <summary>
/// Represents a single file entry in a workspace snapshot manifest.
/// </summary>
/// <param name="Path">
/// The normalised, forward-slash-separated relative path of the file within the workspace root.
/// </param>
/// <param name="AbsolutePath">
/// The OS-specific absolute path of the file, used for streaming blob reads during snapshot creation.
/// </param>
/// <param name="ContentHash">The SHA-256 digest of the raw file bytes.</param>
/// <param name="Size">The raw byte size of the file.</param>
public sealed record ManifestEntry(
    string Path,
    string AbsolutePath,
    byte[] ContentHash,
    long Size
);
