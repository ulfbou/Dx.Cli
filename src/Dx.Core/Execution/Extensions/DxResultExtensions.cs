using Dx.Core.Execution.Results;
using Dx.Core.Protocol;

namespace Dx.Core.Execution.Extensions;

// Create ToResult extension methods from relevant types, e.g., ParseError, ValidationError, etc. as a compliment to the DxResultMapper static methods.
public static class DxResultExtensions
{
    public static DxResult ToResult(this ParseError error)
    {
        var diagnostic = new DxDiagnostic(
            code: "DX001", // Parse Authority Code
            message: error.Message,
            severity: DxDiagnosticSeverity.Error,
            location: $"Line: {error.LineNumber}"
        );

        return new DxResult(
            DxResultStatus.ValidationFailure,
            "Document validation failed.",
            null,
            new[] { diagnostic },
            IsDryRun: false
        );
    }
}
