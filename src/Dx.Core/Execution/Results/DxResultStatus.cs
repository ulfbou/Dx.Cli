namespace Dx.Core.Execution.Results;

/// <summary>
/// Defines the terminal, high-level outcome of a Dx execution.
/// </summary>
/// <remarks>
/// <para>
/// This enum represents the authoritative execution state and is intended
/// to be consumed by CLI tooling, CI pipelines, and automation layers.
/// </para>
/// <para>
/// Individual failure details are provided via
/// <see cref="DxResult.Diagnostics"/>, while this value enables
/// fast branching and exit-code mapping.
/// </para>
/// </remarks>
public enum DxResultStatus
{
    /// <summary>
    /// Execution completed successfully without blocking diagnostics.
    /// </summary>
    Success,

    /// <summary>
    /// Execution failed because the expected base snapshot
    /// does not match the actual snapshot encountered.
    /// </summary>
    BaseMismatch,

    /// <summary>
    /// Execution did not proceed because one or more validation
    /// checks failed prior to execution.
    /// </summary>
    ValidationFailure,

    /// <summary>
    /// Execution terminated due to an unrecoverable runtime failure.
    /// </summary>
    ExecutionFailure,

    /// <summary>
    /// Execution was evaluated in dry-run mode and produced
    /// no material side effects.
    /// </summary>
    DryRun
}
