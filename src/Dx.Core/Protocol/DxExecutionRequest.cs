using System;
using System.Threading;

using Dx.Core.Protocol;

namespace Dx.Core.Execution;

/// <summary>
/// Represents a fully encapsulated request to execute a DX document.
/// </summary>
/// <param name="Document">The semantic model of the document to execute.</param>
/// <param name="RawText">The authoritative raw text of the document.</param>
/// <param name="Direction">The accountability metadata indicating the source of the request.</param>
/// <param name="Mode">The execution mode (e.g., dry run, apply).</param>
/// <param name="Progress">An optional progress reporter for execution feedback.</param>
/// <param name="Options">Optional apply options for execution.</param>
/// <param name="CancellationToken">A cancellation token to observe while executing.</param>
/// <param name="Operation">An optional tool operation name, if this request is a tool operation.</param>
/// <param name="OperationArg">An optional argument for the tool operation.</param>
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
    IProgress<string>? Progress,
    ApplyOptions? Options,
    CancellationToken CancellationToken,
    string? Operation = null,
    string? OperationArg = null)
{
    /// <summary>
    /// Indicates a non-document tool operation executed under dispatcher authority.
    /// </summary>
    public bool IsToolOperation => !string.IsNullOrWhiteSpace(Operation);

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
    /// <param name="progress">When this method returns, contains the progress reporter, or <see langword="null"/> if not set.</param>
    /// <param name="options">When this method returns, contains the apply options, or <see langword="null"/> if not specified.</param>
    /// <param name="ct">When this method returns, contains the cancellation token associated with the operation.</param>
    /// <param name="operation">When this method returns, contains the tool operation name, or <see langword="null"/> if not specified.</param>
    /// <param name="operationArg">When this method returns, contains the tool operation argument, or <see langword="null"/> if not specified.</param>
    public void Deconstruct(
        out DxDocument document,
        out string rawText,
        out string direction,
        out DxExecutionMode mode,
        out IProgress<string>? progress,
        out ApplyOptions? options,
        out CancellationToken ct,
        out string? operation,
        out string? operationArg)
    {
        document = Document;
        rawText = RawText;
        direction = Direction;
        mode = Mode;
        progress = Progress;
        options = Options;
        ct = CancellationToken;
        operation = Operation;
        operationArg = OperationArg;
    }
}
