namespace Dx.Core;

/// <summary>
/// Represents a single changed file entry in a snapshot diff, as returned by
/// <see cref="DxRuntime.Diff"/>.
/// </summary>
/// <param name="Path">The normalised, forward-slash-separated relative path of the changed file.</param>
/// <param name="Status">The nature of the change.</param>
public sealed record DiffEntry(string Path, DiffStatus Status);
