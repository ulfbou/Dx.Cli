namespace Dx.Core;

/// <summary>
/// Internal Dapper mapping record for file manifest rows retrieved during a restore operation.
/// </summary>
/// <param name="Path">The normalised, forward-slash-separated relative path of the file.</param>
/// <param name="ContentHash">The SHA-256 content hash of the file as stored in the blob store.</param>
/// <param name="SizeBytes">The raw byte size of the file at snapshot time.</param>
internal sealed record FileManifestRow(string Path, byte[] ContentHash, long SizeBytes);
