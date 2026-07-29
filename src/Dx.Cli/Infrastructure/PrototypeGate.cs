namespace Dx.Cli.Infrastructure;

using Dx.Core;
using System.Collections.Generic;

/// <summary>
/// Centralised prototype gating policy. Commands listed as unavailable will be
/// blocked at CLI entry so their implementation code remains present but is not
/// executed during the prototype release.
/// </summary>
internal static class PrototypeGate
{
    // Disabled commands for the locked prototype. Keys use a simple naming
    // convention: top-level command names (e.g. "run", "eval", "config") and
    // compound names for subcommands (e.g. "session:new").
    private static readonly HashSet<string> _disabled = new(StringComparer.Ordinal)
    {
        "run",
        "eval",
        "session:new",
        "session:close",
        "config",
    };

    /// <summary>
    /// Enforce the gate for the given command identifier.
    /// If the command is disabled this method writes a single concise message to
    /// stderr and returns the canonical feature-unavailable exit code. Otherwise
    /// returns 0.
    /// </summary>
    public static int Enforce(string commandId)
    {
        if (!_disabled.Contains(commandId))
            return 0;

        // Single canonical concise message required by policy. Write to stderr
        // so piped stdout is not contaminated.
        Console.Error.WriteLine($"error: command '{commandId}' is unavailable in the prototype release");

        // Use the canonical feature-unavailable identity.
        return DxException.ExitCode(DxError.FeatureUnavailable);
    }
}
