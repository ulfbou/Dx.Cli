using System;

using Dx.Core.Execution.Results;
using Dx.Core.Protocol;

namespace Dx.Core.Execution.Adapters;

/// <summary>
/// Normalizes protocol-level parse errors and engine-level diagnostics into a
/// unified <see cref="DxResult"/> status.
/// </summary>
public static class DxResultMapper
{
    /// <summary>
    /// Creates a successful result with the given snapshot ID.
    /// </summary>
    public static DxResult FromSuccess(string? snapId) =>
        new(DxResultStatus.Success, null, snapId, Array.Empty<DxDiagnostic>(), false);

    /// <summary>
    /// Creates a base-mismatch failure result.
    /// </summary>
    public static DxResult FromBaseMismatch(string expected, string actual)
    {
        var diagnostic = new DxDiagnostic(
            code: "BASE_MISMATCH",
            message: $"Base mismatch. Expected: {expected}, Actual: {actual}",
            severity: DxDiagnosticSeverity.Error);
        return new DxResult(
            Status: DxResultStatus.BaseMismatch,
            Message: diagnostic.Message,
            SnapId: null,
            Diagnostics: new[] { diagnostic },
            IsDryRun: false);
    }

    /// <summary>
    /// Creates a dry-run result.
    /// </summary>
    public static DxResult FromDryRun() =>
        new(DxResultStatus.DryRun, null, null, Array.Empty<DxDiagnostic>(), true);

    /// <summary>
    /// Creates an execution-failure result from an exception.
    /// </summary>
    public static DxResult FromExecutionException(Exception ex)
    {
        ArgumentNullException.ThrowIfNull(ex);
        var diagnostic = new DxDiagnostic(
            code: "EXECUTION_FAILURE",
            message: ex.Message,
            severity: DxDiagnosticSeverity.Error);
        return new DxResult(
            Status: DxResultStatus.ExecutionFailure,
            Message: ex.Message,
            SnapId: null,
            Diagnostics: new[] { diagnostic },
            IsDryRun: false);
    }

    /// <summary>
    /// Promotes a collection of <see cref="ParseError"/> objects to <see cref="DxDiagnostic"/>
    /// and maps them to a failure result.
    /// </summary>
    public static DxResult FromParseErrors(IEnumerable<ParseError> errors)
    {
        var diagnostics = errors.Select(e => new DxDiagnostic(
            code: "DX-PARSE-001",
            message: e.Message,
            severity: DxDiagnosticSeverity.Error,
            location: $"Line: {e.LineNumber}"));

        return FromValidationErrors(diagnostics);
    }

    /// <summary>
    /// Maps validation diagnostics to a <see cref="DxResult"/>.
    /// </summary>
    public static DxResult FromValidationErrors(IEnumerable<DxDiagnostic>? diagnostics)
    {
        return new DxResult(
            Status: DxResultStatus.ValidationFailure,
            Message: "Document validation failed.",
            SnapId: null,
            Diagnostics: diagnostics?.ToArray() ?? Array.Empty<DxDiagnostic>(),
            IsDryRun: false);
    }
}