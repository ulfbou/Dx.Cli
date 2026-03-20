using Dx.Core;

using Spectre.Console;
using Spectre.Console.Cli;

namespace Dx.Cli.Commands;

/// <summary>
/// Provides an abstract base class for all <c>dxs</c> CLI commands, encapsulating
/// shared utilities for workspace root discovery and standardised error handling.
/// </summary>
/// <typeparam name="TSettings">
/// The concrete <see cref="CommandSettings"/> type that carries the parsed arguments
/// and options for the derived command.
/// </typeparam>
public abstract class DxCommandBase<TSettings> : AsyncCommand<TSettings>
    where TSettings : CommandSettings
{
    /// <summary>
    /// Resolves the workspace root directory to use for the current command invocation.
    /// </summary>
    /// <param name="explicitRoot">
    /// An explicit root path supplied by the user via <c>--root</c>, or <see langword="null"/>
    /// to trigger automatic discovery.
    /// </param>
    /// <returns>
    /// The absolute path of the resolved workspace root. When <paramref name="explicitRoot"/>
    /// is provided it is returned as-is (after normalisation). Otherwise, the method walks up
    /// the directory tree from the current working directory, returning the first ancestor that
    /// contains a <c>.dx/</c> sub-folder. If no such ancestor is found, the current working
    /// directory is returned as a fallback.
    /// </returns>
    protected static string FindRoot(string? explicitRoot)
    {
        if (explicitRoot is not null)
            return Path.GetFullPath(explicitRoot);

        // Walk up from CWD looking for .dx/
        var dir = Directory.GetCurrentDirectory();
        while (true)
        {
            if (Directory.Exists(Path.Combine(dir, ".dx")))
                return dir;
            var parent = Path.GetDirectoryName(dir);
            if (parent is null || parent == dir) break;
            dir = parent;
        }

        return Directory.GetCurrentDirectory();
    }

    /// <summary>
    /// Handles a <see cref="DxException"/> by printing a formatted error message to the
    /// console and returning the appropriate process exit code.
    /// </summary>
    /// <param name="ex">The <see cref="DxException"/> to handle.</param>
    /// <returns>
    /// The exit code associated with <see cref="DxException.Error"/>, as determined by
    /// <see cref="DxException.ExitCode"/>.
    /// </returns>
    protected static int HandleDxException(DxException ex)
    {
        AnsiConsole.MarkupLine($"[red]error:[/] {ex.Message}");
        return DxException.ExitCode(ex.Error);
    }

    /// <summary>
    /// Handles an unexpected <see cref="Exception"/> by printing a formatted error message
    /// to the console and returning exit code <c>1</c>.
    /// </summary>
    /// <param name="ex">The unexpected exception to handle.</param>
    /// <returns>Always returns <c>1</c>.</returns>
    protected static int HandleUnexpected(Exception ex)
    {
        AnsiConsole.MarkupLine($"[red]unexpected error:[/] {ex.Message}");
        return 1;
    }
}
