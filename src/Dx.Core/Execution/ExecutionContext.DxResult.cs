using System;

using Dx.Core.Execution.Results;

namespace Dx.Core.Execution;

/// <summary>
/// Execution context extension responsible for capturing
/// the terminal execution result.
/// </summary>
public partial class ExecutionContext
{
    /// <summary>
    /// Gets the final, authoritative outcome of the execution.
    /// </summary>
    /// <value>
    /// Returns the assigned <see cref="DxResult"/>; otherwise
    /// <see langword="null"/> if execution is still in progress.
    /// </value>
    public DxResult? DxResult { get; private set; }

    /// <summary>
    /// Transitions the context into a terminal state by assigning
    /// the execution result.
    /// </summary>
    /// <param name="result">
    /// The immutable <see cref="DxResult"/> to associate with this execution.
    /// Must not be <see langword="null"/>.
    /// </param>
    /// <remarks>
    /// <para>
    /// <b>Lifecycle Contract:</b> This method enforces a single-assignment rule.
    /// Once called successfully, the execution is considered complete and
    /// the result must not be replaced.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="result"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// Thrown if a result has already been assigned.
    /// </exception>
    public void SetDxResult(DxResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        if (DxResult != null)
            throw new InvalidOperationException(
                "Execution result is final and cannot be reassigned.");

        DxResult = result;
    }
}
