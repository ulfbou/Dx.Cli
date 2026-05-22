namespace Dx.Core.Protocol;

/// <summary>
/// Defines the execution mode of a DX request.
/// </summary>
public enum DxExecutionMode
{
    /// <summary>
    /// Executes without mutating any state.
    /// </summary>
    DryRun = 0,

    /// <summary>
    /// Executes with full transactional semantics.
    /// </summary>
    Apply = 1,

    /// <summary>
    /// Read-only execution: only <c>%%REQUEST</c> blocks are evaluated.
    /// No working-tree mutation, no snapshot, no <c>session_state</c> write.
    /// </summary>
    Request = 2
}
