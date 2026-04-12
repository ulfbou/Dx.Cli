using System;
using System.Threading;

using Dx.Core.Protocol;

namespace Dx.Core.Execution;

/// <summary>
/// Represents the complete, immutable execution request
/// for a DX operation crossing the protocol boundary.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="DxExecutionRequest"/> is the sole input contract
/// for all DX execution. No execution path may bypass this type.
/// </para>
/// <para>
/// This object is intentionally immutable and validated at
/// construction time to prevent partial or inconsistent execution.
/// </para>
/// </remarks>
public sealed class DxExecutionRequest
{
    /// <summary>Gets the parsed DX document to execute.</summary>
    public DxDocument Document { get; }

    /// <summary>Gets the execution mode.</summary>
    public DxExecutionMode Mode { get; }

    /// <summary>Gets the optional progress reporter.</summary>
    public IProgress<string>? Progress { get; }

    /// <summary>Gets the optional execution overrides.</summary>
    public ApplyOptions? Options { get; }

    /// <summary>Gets the cancellation token for this execution.</summary>
    public CancellationToken CancellationToken { get; }

    /// <summary>
    /// Initializes a new <see cref="DxExecutionRequest"/>.
    /// </summary>
    /// <param name="document">The parsed DX document.</param>
    /// <param name="mode">The execution mode.</param>
    /// <param name="progress">Optional progress sink.</param>
    /// <param name="options">Optional per-invocation overrides.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="document"/> is null.
    /// </exception>
    public DxExecutionRequest(
        DxDocument document,
        DxExecutionMode mode,
        IProgress<string>? progress = null,
        ApplyOptions? options = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        Document = document;
        Mode = mode;
        Progress = progress;
        Options = options;
        CancellationToken = ct;
    }

    /// <summary>
    /// Gets a value indicating whether this request is a dry run.
    /// </summary>
    public bool IsDryRun => Mode == DxExecutionMode.DryRun;
}
