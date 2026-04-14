using System;
using System.Threading;

using Dx.Core.Protocol;

namespace Dx.Core.Execution;

/// <summary>
/// Represents a fully encapsulated request to execute a DX document.
/// </summary>
/// <remarks>
/// <para>
/// This record acts as the execution envelope, separating the semantic model 
/// (<see cref="Document"/>) from the authoritative intent (<see cref="RawText"/>) 
/// and accountability metadata (<see cref="Direction"/>).
/// </para>
/// <para>
/// <strong>Boundary:</strong>
/// This type is the only location where execution intent 
/// (<see cref="RawText"/>) and accountability metadata 
/// (<see cref="Direction"/>) are stored.
/// </para>
/// <para>
/// These values must not be copied into semantic models 
/// (<see cref="DxDocument"/>) or execution outcomes 
/// (<see cref="DxResult"/>).
/// </para>
/// </remarks>
public sealed record DxExecutionRequest(
    DxDocument Document,
    string RawText,
    string Direction,
    DxExecutionMode Mode,
    bool IsDryRun, // This property is redundant but included for convenience and clarity.
    IProgress<string>? Progress,
    ApplyOptions? Options,
    CancellationToken CancellationToken)
{
    /// <summary>
    /// Gets a value indicating whether this request is a dry run.
    /// </summary>
    public bool IsDryRun => Mode == DxExecutionMode.DryRun;

    /// <summary>
    /// Deconstructs the execution request into its component properties, allowing for deconstruction assignment syntax
    /// (e.g. <c>var (document, mode, isDryRun, progress, options, ct) = request;</c>).
    /// </summary>
    /// <param name="document">When this method returns, contains the value of the Document property.</param>
    /// <param name="rawText">When this method returns, contains the value of the RawText property.</param>
    /// <param name="direction">When this method returns, contains the value of the Direction
    /// <param name="mode">When this method returns, contains the value of the Mode property.</param>
    /// <param name="isDryRun">When this method returns, contains a value indicating whether the operation is a dry run.</param>
    /// <param name="progress">When this method returns, contains the progress reporter, or null if not set.</param>
    /// <param name="options">When this method returns, contains the apply options, or null if not specified.</param>
    /// <param name="ct">When this method returns, contains the cancellation token associated with the operation.</param>
    public void Deconstruct(
        out DxDocument document,
        out string rawText,
        out string direction,
        out DxExecutionMode mode,
        out bool isDryRun,
        out IProgress<string>? progress,
        out ApplyOptions? options,
        out CancellationToken ct)
    {
        document = Document;
        rawText = RawText;
        direction = Direction;
        mode = Mode;
        isDryRun = IsDryRun;
        progress = Progress;
        options = Options;
        ct = CancellationToken;
    }
}
