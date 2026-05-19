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
            IsDryRun: false // Dispatcher will set correctly during execution
        );
    }

    /// <summary>
    /// Maps a legacy <see cref="DispatchResult"/> to an authoritative <see cref="DxResult"/>.
    /// </summary>
    /// <param name="legacy">The legacy <see cref="DispatchResult"/> to convert.</param>
    /// <param name="isDryRun">Indicates whether the result is from a dry run execution.</param>
    /// <returns>A new <see cref="DxResult"/> instance representing the same outcome as the legacy result.</returns>
    /// <remarks>
    /// <strong>Property Alignment:</strong>
    /// <list type="bullet">
    /// <item><description><c>Error</c> (Internal) -> <c>Message</c> (Canonical)</description></item>
    /// <item><description><c>NewHandle</c> (Internal) -> <c>SnapId</c> (Canonical)</description></item>
    /// </list>
    /// </remarks>
    [Obsolete("Temporary bridge. Remove in PR 45 when DispatchResult is retired.")]
    public static DxResult ToDxResult(this DispatchResult legacy, bool isDryRun)
    {
        var status = legacy.Success ? DxResultStatus.Success
                   : legacy.IsBaseMismatch ? DxResultStatus.BaseMismatch
                   : DxResultStatus.ExecutionFailure;

        var diagnostics = new List<DxDiagnostic>();

        // GOLDEN INVARIANT: If it failed, there MUST be a diagnostic.
        if (!legacy.Success)
        {
            diagnostics.Add(new DxDiagnostic(
                code: legacy.IsBaseMismatch ? "BASE_MISMATCH" : "EXECUTION_ERROR",
                message: legacy.Error ?? "An unknown execution error occurred.",
                severity: DxDiagnosticSeverity.Error
            ));
        }

        return new DxResult(
            Status: status,
            Message: legacy.Error,
            SnapId: legacy.NewHandle,
            Diagnostics: diagnostics,
            IsDryRun: isDryRun,
            Metadata: null,
            Blocks: legacy.Operations
        );
    }
}
