using Dapper;

using Microsoft.Data.Sqlite;

namespace Dx.Core;

/// <summary>
/// Represents a row from the <c>snap_handles</c> table, coupling a snapshot hash to its
/// human-readable session-scoped handle and sequence number.
/// </summary>
/// <param name="Handle">The human-readable handle string (e.g. <c>T0003</c>).</param>
/// <param name="SnapHash">The raw 32-byte SHA-256 hash of the snapshot.</param>
/// <param name="Seq">The zero-based sequence number within the session.</param>
/// <param name="CreatedUtc">The ISO 8601 UTC timestamp at which the handle was assigned.</param>
public sealed record SnapHandleRow(string Handle, byte[] SnapHash, int Seq, string CreatedUtc);
