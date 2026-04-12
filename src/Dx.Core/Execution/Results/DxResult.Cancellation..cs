using Dx.Core.Protocol;

namespace Dx.Core.Execution.Results;

public sealed partial class DxResult
{
    /// <summary>
    /// Creates a standardized cancellation result.
    /// </summary>
    /// <param name="mode">The execution mode during which cancellation occurred.</param>
    /// <returns>A <see cref="DxResult"/> representing a cancelled execution.</returns>
    public static DxResult Canceled(DxExecutionMode mode)
        => new(
            DxResultStatus.ExecutionFailure,
            "Execution cancelled",
            snapId: null,
            diagnostics: new[]
            {
                new DxDiagnostic(
                    "EXECUTION_CANCELLED",
                    "Execution was cancelled by the caller.",
                    DxDiagnosticSeverity.Error)
            },
            isDryRun: mode == DxExecutionMode.DryRun);
}
