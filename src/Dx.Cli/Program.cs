using Dx.Cli.Commands;
using Spectre.Console.Cli;

var app = new CommandApp();

app.Configure(config =>
{
    config.SetApplicationName("dx");
    config.SetApplicationVersion("0.1.0");
    config.UseStrictParsing();

    config.AddCommand<InitCommand>("init")
          .WithDescription("Initialize a DX workspace and take genesis snap T0000.");

    config.AddCommand<ApplyCommand>("apply")
          .WithDescription("Apply a DX document to the working tree.");

    config.AddBranch("snap", snap =>
    {
        snap.SetDescription("Manage snapshots.");
        snap.AddCommand<SnapListCommand>("list")
            .WithDescription("List snaps in the current session.");
        snap.AddCommand<SnapShowCommand>("show")
            .WithDescription("Show details for a specific snap.");
        snap.AddCommand<SnapDiffCommand>("diff")
            .WithDescription("Diff two snaps.");
        snap.AddCommand<SnapCheckoutCommand>("checkout")
            .WithDescription("Restore working tree to a snap state.");
    });

    config.AddCommand<LogCommand>("log")
          .WithDescription("Show session log.");

    config.AddCommand<PackCommand>("pack")
          .WithDescription("Bundle files into a read-only DX document for LLM context.");

#if DEBUG
    config.PropagateExceptions();
    config.ValidateExamples();
#endif
});

return app.Run(args);
