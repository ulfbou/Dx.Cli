namespace Dx.Core.Genesis;

/// <summary>
/// Returned after creation of a genesis snapshot.
/// </summary>
/// <param name="SessionId">The session ID of the newly created session.</param>
/// <param name="GenesisHandle">The handle assigned to the genesis snapshot (T0000).</param>
/// <param name="FileCount">The number of files included in the genesis snapshot.</param>
/// <param name="SnapHash">The hash of the genesis snapshot.</param>
public sealed record SessionGenesisResult(
    string SessionId,
    string GenesisHandle,
    int FileCount,
    byte[] SnapHash);
