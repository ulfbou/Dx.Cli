using System;
using System.Collections.Generic;
using System.Linq;

using Dx.Core.Execution.Results;

namespace Dx.Core.Execution.Adapters;

/// <summary>
/// Provides canonical, defensive factory methods for creating
/// <see cref="DxResult"/> instances from execution outcomes.
/// </summary>
/// <remarks>
/// Consumers should prefer these methods over direct construction
/// to ensure consistent diagnostics and status mapping.
/// </remarks>
public static class DxResultMapper
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
    public static DxResult FromSuccess(string? snapId) =>
        new(DxResultStatus.Success, null, snapId, Array.Empty<DxDiagnostic>(), false);

    /// <summary>
    /// Creates a failure result for base snapshot mismatches.
    /// </summary>
    /// <param name="expected">The expected baseline identifier.</param>
    /// <param name="actual">The actual snapshot identifier found.</param>
    /// <returns>
    /// A result with <see cref="DxResultStatus.BaseMismatch"/> and
    /// a single blocking diagnostic.
    /// </returns>
    public static DxResult FromBaseMismatch(string? expected, string? actual)
    {
        var diagnostics = new[]
        {
            new DxDiagnostic(
                "BASE_MISMATCH",
                $"Base mismatch. Expected: {expected ?? "unknown"}, Actual: {actual ?? "unknown"}",
                DxDiagnosticSeverity.Error)
        };

        return new DxResult(
            DxResultStatus.BaseMismatch,
            "Base mismatch",
            null,
            diagnostics,
            false);
    }

    /// <summary>
    /// Creates a validation failure result.
    /// </summary>
    /// <param name="diagnostics">
    /// A collection of validation diagnostics.
    /// </param>
    /// <returns>
    /// A result with <see cref="DxResultStatus.ValidationFailure"/>.
    /// </returns>
    public static DxResult FromValidationErrors(IEnumerable<DxDiagnostic>? diagnostics)
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
    public static DxResult FromExecutionException(Exception ex)
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
            false);
    }

    /// <summary>
    /// Creates a dry-run execution result.
    /// </summary>
    /// <returns>
    /// A result with <see cref="DxResultStatus.DryRun"/> and no diagnostics.
    /// </returns>
    public static DxResult FromDryRun() =>
        new(DxResultStatus.DryRun, "Dry run", null, Array.Empty<DxDiagnostic>(), true);
}
