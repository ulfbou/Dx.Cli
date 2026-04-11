using System;

namespace Dx.Core.Execution.Results;

/// <summary>
/// Represents a single, immutable diagnostic event captured during execution.
/// </summary>
/// <remarks>
/// <para>
/// Diagnostics are designed for machine consumption.
/// While <see cref="Message"/> is intended for humans,
/// the <see cref="Code"/> is the stable contract for automated
/// branching, telemetry, and policy enforcement.
/// </para>
/// <para>
/// Consumers must never infer behavior from message text.
/// All programmatic logic must rely on <see cref="Code"/> and
/// <see cref="Severity"/>.
/// </para>
/// </remarks>
public sealed class DxDiagnostic
{
    /// <value>
    /// A stable, unique diagnostic identifier (for example, "DX001" or "BASE_MISMATCH").
    /// Guaranteed to be non-null and non-whitespace.
    /// </value>
    public string Code { get; }

    /// <value>
    /// A human-readable explanation of the diagnostic.
    /// Subject to localization and formatting changes.
    /// </value>
    public string Message { get; }

    /// <value>
    /// An optional physical path, line reference, or logical scope describing
    /// where the diagnostic originated.
    /// Returns <see langword="null"/> for global diagnostics.
    /// </value>
    public string? Location { get; }

    /// <value>
    /// The severity that determines whether execution should halt.
    /// </value>
    public DxDiagnosticSeverity Severity { get; }

    /// <summary>
    /// Initializes a new instance of the <see cref="DxDiagnostic"/> class.
    /// </summary>
    /// <param name="code">
    /// A stable, alphanumeric identifier.
    /// Must not be <see langword="null"/>, empty, or whitespace.
    /// </param>
    /// <param name="message">
    /// A human-readable explanation of the diagnostic.
    /// Must not be <see langword="null"/>, empty, or whitespace.
    /// </param>
    /// <param name="severity">
    /// The <see cref="DxDiagnosticSeverity"/> determining execution impact.
    /// </param>
    /// <param name="location">
    /// An optional path, line number, or logical scope identifier.
    /// </param>
    /// <exception cref="ArgumentException">
    /// Thrown when <paramref name="code"/> or <paramref name="message"/>
    /// is null, empty, or consists only of whitespace.
    /// </exception>
    public DxDiagnostic(
        string code,
        string message,
        DxDiagnosticSeverity severity,
        string? location = null)
    {
        if (string.IsNullOrWhiteSpace(code))
            throw new ArgumentException("Diagnostic code must be provided.", nameof(code));

        if (string.IsNullOrWhiteSpace(message))
            throw new ArgumentException("Diagnostic message must be provided.", nameof(message));

        Code = code;
        Message = message;
        Severity = severity;
        Location = location;
    }
}
