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
public static class DxResultExtensions
{
    /// <summary>
    /// Maps a successful execution into a final result.
    /// </summary>
    /// <param name="snapId">
    /// The identifier of the generated or validated snapshot.
    /// </param>
    /// <returns>
    /// A <see cref="DxResult"/> with
    /// <see cref="DxResultStatus.Success"/> and no diagnostics.
    /// </returns>
    public static DxResult AsSuccess(this string? snapId) =>
        new(DxResultStatus.Success, null, snapId, Array.Empty<DxDiagnostic>(), false);

    /// <summary>
    /// Creates a validation failure result.
    /// </summary>
    /// <param name="diagnostics">
    /// A collection of validation diagnostics.
    /// </param>
    /// <returns>
    /// A result with <see cref="DxResultStatus.ValidationFailure"/>.
    /// </returns>
    public static DxResult AsValidationFailure(this IEnumerable<DxDiagnostic>? diagnostics)
    {
        var diagList = diagnostics?.ToList() ?? new List<DxDiagnostic>();

        return new DxResult(
            DxResultStatus.ValidationFailure,
            "One or more validation errors occurred.",
            null,
            diagList,
            false);
    }

    /// <summary>
    /// Creates an execution failure result from an exception.
    /// </summary>
    /// <param name="ex">The caught execution exception.</param>
    /// <returns>
    /// A result with <see cref="DxResultStatus.ExecutionFailure"/>.
    /// </returns>
    /// <exception cref="ArgumentNullException">
    /// Thrown if <paramref name="ex"/> is <see langword="null"/>.
    /// </exception>
    public static DxResult AsExecutionFailure(this DxException ex, bool isDryRun = false)
    {
        ArgumentNullException.ThrowIfNull(ex);

        return new DxResult(
            DxResultStatus.ExecutionFailure,
            "Execution failed",
            null,
            new[]
            {
                new DxDiagnostic(
                    "EXECUTION_FAILURE",
                    ex.Message,
                    DxDiagnosticSeverity.Error)
            },
            IsDryRun: isDryRun);
    }
    public static DxResult AsExecutionFailure(this Exception ex, bool isDryRun = false)
    {
        var diagnostics = new List<DxDiagnostic> {
            new DxDiagnostic("DX999", ex.Message, DxDiagnosticSeverity.Error)
        };

        return new DxResult(
            DxResultStatus.ExecutionFailure,
            ex.Message,
            null,
            diagnostics,
            IsDryRun: isDryRun
        );
    }

    /// <summary>
    /// Creates a standardized cancellation result.
    /// </summary>
    /// <param name="mode">The execution mode during which cancellation occurred.</param>
    /// <returns>A <see cref="DxResult"/> representing a cancelled execution.</returns>
    public static DxResult AsCanceled(this DxExecutionMode mode)
        => new(
            DxResultStatus.ExecutionFailure,
            "Execution cancelled",
            SnapId: null,
            Diagnostics: new[]
            {
                new DxDiagnostic(
                    "EXECUTION_CANCELLED",
                    "Execution was cancelled by the caller.",
                    DxDiagnosticSeverity.Error)
            },
            IsDryRun: mode == DxExecutionMode.DryRun);

    public static DxResult AsValidationFailure(this IEnumerable<ParseError> errors, bool isDryRun = false)
    {
        var diagnostics = errors.Select(e => new DxDiagnostic(
            code: "DX001", // Parse Authority Code
            message: e.Message,
            severity: DxDiagnosticSeverity.Error,
            location: $"Line: {e.LineNumber}")
        ).ToList();

        return new DxResult(
            DxResultStatus.ValidationFailure,
            "Document validation failed.",
            null,
            diagnostics,
            IsDryRun: false // Dispatcher will set correctly during execution
        );
    }
    
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
    public static DxResult AsResult(this DispatchResult legacy, bool isDryRun)
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