namespace Dx.Core.Protocol;

/// <summary>
/// Represents a single parse error encountered by <see cref="DxParser"/>, identifying
/// the source line and a human-readable description of the problem.
/// </summary>
/// <param name="LineNumber">The 1-based line number in the source document where the error occurred.</param>
/// <param name="Message">A human-readable description of the parse error.</param>
public sealed record ParseError(int LineNumber, string Message);
