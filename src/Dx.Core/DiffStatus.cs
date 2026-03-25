namespace Dx.Core;

/// <summary>Classifies the change status of a file between two snapshots.</summary>
public enum DiffStatus
{
    /// <summary>The file exists in the candidate snapshot but not in the baseline.</summary>
    Added,

    /// <summary>The file exists in both snapshots but its content differs.</summary>
    Modified,

    /// <summary>The file exists in the baseline snapshot but not in the candidate.</summary>
    Deleted,

    /// <summary>The file exists in both snapshots and its content is identical.</summary>
    Unchanged,
}
