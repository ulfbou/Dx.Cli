namespace Dx.Core.Execution.Results;

/// <summary>
/// Specifies the severity level of a diagnostic emitted during execution.
/// </summary>
/// <remarks>
/// Severity determines whether a diagnostic is blocking
/// or purely informational.
/// </remarks>
public enum DxDiagnosticSeverity
{
    /// <summary>
    /// Indicates a blocking condition that renders execution invalid
    /// or failed.
    /// </summary>
    Error,

    /// <summary>
    /// Indicates a non-blocking condition that may require
    /// user attention but does not invalidate execution.
    /// </summary>
    Warning
}
