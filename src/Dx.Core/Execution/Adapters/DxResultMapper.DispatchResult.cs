// src/Dx.Core/Execution/Adapters/DxResultMapper.DispatchResult.cs
using Dx.Core.Execution.Results;
using Dx.Core.Protocol;

namespace Dx.Core.Execution.Adapters;

/// <summary>
/// Transitional mapper to convert legacy <see cref="DispatchResult"/> 
/// into the canonical <see cref="DxResult"/> contract.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Lifecycle:</strong>
/// This mapper exists solely to bridge legacy transaction results 
/// during the execution pipeline migration.
/// </para>
/// <para>
/// <strong>Removal Condition:</strong>
/// Remove this class once internal execution layers return 
/// <see cref="DxResult"/> natively (planned in PR 45).
/// </para>
/// </remarks>
public static partial class DxResultMapper
{
    /// <summary>
    /// Maps a legacy <see cref="DispatchResult"/> to an authoritative <see cref="DxResult"/>.
    /// </summary>
    /// <remarks>
    /// <strong>Property Alignment:</strong>
    /// <list type="bullet">
    /// <item><description><c>Error</c> (Internal) -> <c>Message</c> (Canonical)</description></item>
    /// <item><description><c>NewHandle</c> (Internal) -> <c>SnapId</c> (Canonical)</description></item>
    /// </list>
    /// </remarks>
    [Obsolete("Temporary bridge. Remove in PR 45 when DispatchResult is retired.")]
    public static DxResult ToDxResult(DispatchResult legacy, bool isDryRun)
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
            status: status,
            message: legacy.Error,
            snapId: legacy.NewHandle,
            diagnostics: diagnostics,
            isDryRun: isDryRun,
            metadata: null,
            blocks: legacy.Operations
        );
    }
}
