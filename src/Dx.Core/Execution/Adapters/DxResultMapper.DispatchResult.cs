// src/Dx.Core/Execution/Adapters/DxResultMapper.DispatchResult.cs
using Dx.Core.Execution.Results;
using Dx.Core.Protocol;

namespace Dx.Core.Execution.Adapters;

/// <summary>
/// Provides mapping from internal dispatch results to protocol-level results.
/// </summary>
public static partial class DxResultMapper
{
    /// <summary>
    /// Converts a <see cref="DispatchResult"/> into a canonical <see cref="DxResult"/>.
    /// </summary>
    /// <param name="raw">The raw dispatch result.</param>
    /// <param name="isDryRun">Indicates whether execution was a dry run.</param>
    /// <returns>A normalized <see cref="DxResult"/>.</returns>
    public static DxResult FromDispatchResult(
        DispatchResult raw,
        bool isDryRun)
    {
        if (raw.Success)
        {
            if (isDryRun)
                return FromDryRun();

            return FromSuccess(raw.NewHandle);
        }

        if (raw.IsBaseMismatch)
        {
            return new DxResult(
                DxResultStatus.BaseMismatch,
                raw.Error,
                snapId: null,
                diagnostics: new[]
                {
                    new DxDiagnostic(
                        "BASE_MISMATCH",
                        raw.Error ?? "Base mismatch",
                        DxDiagnosticSeverity.Error)
                },
                isDryRun: false);
        }

        return new DxResult(
            DxResultStatus.ExecutionFailure,
            raw.Error ?? "Execution failed",
            snapId: null,
            diagnostics: new[]
            {
                new DxDiagnostic(
                    "EXECUTION_FAILURE",
                    raw.Error ?? "Execution failed",
                    DxDiagnosticSeverity.Error)
            },
            isDryRun: false);
    }
}
