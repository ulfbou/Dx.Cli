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
    public static DxResult FromValidationErrors(IEnumerable<DxDiagnostic> diagnostics)
    {
        return new DxResult(
            Status: DxResultStatus.ValidationFailure,
            Message: "Document validation failed.",
            SnapId: null,
            Diagnostics: diagnostics.ToList(),
            IsDryRun: false);
    }
}
