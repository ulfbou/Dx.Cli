namespace Dx.Core;

/// <summary>
/// Represents a single file entry in a snapshot manifest, as returned by
/// <see cref="DxRuntime.GetSnapFiles"/>.
/// </summary>
/// <param name="Path">The normalised, forward-slash-separated relative path of the file.</param>
/// <param name="SizeBytes">The raw byte size of the file at the time the snapshot was taken.</param>
public sealed record SnapFileInfo(string Path, long SizeBytes);
