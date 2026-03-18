using Dx.Core;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Dx.Cli.Commands;

public abstract class DxCommandBase<TSettings> : AsyncCommand<TSettings>
    where TSettings : CommandSettings
{
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

    protected static int HandleDxException(DxException ex)
    {
        AnsiConsole.MarkupLine($"[red]error:[/] {ex.Message}");
        return DxException.ExitCode(ex.Error);
    }

    protected static int HandleUnexpected(Exception ex)
    {
        AnsiConsole.MarkupLine($"[red]unexpected error:[/] {ex.Message}");
        return 1;
    }
}
