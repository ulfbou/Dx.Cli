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
    Apply = 1
}
