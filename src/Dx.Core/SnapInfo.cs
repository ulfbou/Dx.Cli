namespace Dx.Core;

// ── DTOs ──────────────────────────────────────────────────────────────────────

/// <summary>
/// Represents the metadata for a single snapshot within a session, as returned by
/// <see cref="DxRuntime.ListSnaps"/>.
/// </summary>
public class SnapInfo
{
    /// <summary>Parameterless constructor required by Dapper for materialisation.</summary>
    public SnapInfo() { }

    /// <summary>Gets or sets the human-readable snapshot handle (e.g. <c>T0003</c>).</summary>
    public string Handle { get; set; } = string.Empty;

    /// <summary>Gets or sets the zero-based sequence number of this snapshot within the session.</summary>
    public long Seq { get; set; }

    /// <summary>Gets or sets the ISO 8601 UTC timestamp at which the snapshot was created.</summary>
    public string CreatedUtc { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets a value indicating whether this snapshot is the current HEAD of its session.
    /// </summary>
    public bool IsHead { get; set; }

    /// <summary>Initialises a fully populated <see cref="SnapInfo"/> instance.</summary>
    public SnapInfo(string handle, long seq, string createdUtc, bool isHead)
    {
        Handle = handle;
        Seq = seq;
        CreatedUtc = createdUtc;
        IsHead = isHead;
    }
}
