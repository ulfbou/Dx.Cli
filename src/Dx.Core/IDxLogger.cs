namespace Dx.Core;

/// <summary>
/// Defines the logging contract used throughout the DX runtime for diagnostic output.
/// </summary>
/// <remarks>
/// All log output is directed to stderr so it does not interfere with structured stdout
/// output (e.g. packed DX documents piped to another tool). Implementations may filter
/// messages by severity or suppress all output entirely.
/// </remarks>
public interface IDxLogger
{
    /// <summary>Logs an informational message describing a normal runtime event.</summary>
    /// <param name="message">The message to log.</param>
    void Info(string message);

    /// <summary>Logs a warning message describing an anomalous but recoverable condition.</summary>
    /// <param name="message">The message to log.</param>
    void Warn(string message);

    /// <summary>Logs an error message describing a failure.</summary>
    /// <param name="message">The message to log.</param>
    void Error(string message);

    /// <summary>
    /// Logs a debug message containing detailed diagnostic information.
    /// Implementations may suppress debug messages unless verbose mode is active.
    /// </summary>
    /// <param name="message">The message to log.</param>
    void Debug(string message);
}

/// <summary>
/// A no-op <see cref="IDxLogger"/> implementation that discards all log messages.
/// Used as the default logger when no external logger is provided.
/// </summary>
public sealed class NullDxLogger : IDxLogger
{
    /// <summary>Gets the singleton instance of <see cref="NullDxLogger"/>.</summary>
    public static readonly NullDxLogger Instance = new();

    /// <inheritdoc />
    public void Info(string _) { }

    /// <inheritdoc />
    public void Warn(string _) { }

    /// <inheritdoc />
    public void Error(string _) { }

    /// <inheritdoc />
    public void Debug(string _) { }
}

/// <summary>
/// An <see cref="IDxLogger"/> implementation that writes all messages to stderr,
/// optionally suppressing <see cref="Debug"/> output based on a verbosity flag.
/// </summary>
/// <param name="verbose">
/// When <see langword="true"/>, debug-level messages are written to stderr in addition
/// to info, warn, and error messages.
/// </param>
public sealed class ConsoleDxLogger(bool verbose = false) : IDxLogger
{
    /// <inheritdoc />
    public void Info(string message)  => Console.Error.WriteLine($"  {message}");

    /// <inheritdoc />
    public void Warn(string message)  => Console.Error.WriteLine($"  warn: {message}");

    /// <inheritdoc />
    public void Error(string message) => Console.Error.WriteLine($"  error: {message}");

    /// <inheritdoc />
    public void Debug(string message) { if (verbose) Console.Error.WriteLine($"  debug: {message}"); }
}
