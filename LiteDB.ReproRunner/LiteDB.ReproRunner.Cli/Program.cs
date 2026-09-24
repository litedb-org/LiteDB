using System.Threading;
using LiteDB.ReproRunner.Cli.Commands;
using LiteDB.ReproRunner.Cli.Execution;
using LiteDB.ReproRunner.Cli.Infrastructure;
using Spectre.Console;
using Spectre.Console.Cli;

namespace LiteDB.ReproRunner.Cli;

/// <summary>
/// Entry point for the repro runner CLI application.
/// </summary>
internal static class Program
{
    /// <summary>
    /// Application entry point.
    /// </summary>
    /// <param name="args">The command-line arguments provided by the user.</param>
    /// <returns>The process exit code.</returns>
    public static async Task<int> Main(string[] args)
    {
        using var cts = new CancellationTokenSource();

        Console.CancelKeyPress += (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            cts.Cancel();
        };

        var (filteredArgs, rootOverride) = ExtractGlobalOptions(args);
        var console = AnsiConsole.Create(new AnsiConsoleSettings());
        var registrar = new TypeRegistrar();
        registrar.RegisterInstance(typeof(IAnsiConsole), console);
        registrar.RegisterInstance(typeof(ReproExecutor), new ReproExecutor());
        registrar.RegisterInstance(typeof(RunDirectoryPlanner), new RunDirectoryPlanner());
        registrar.RegisterInstance(typeof(ReproBuildCoordinator), new ReproBuildCoordinator());
        registrar.RegisterInstance(typeof(ReproRootLocator), new ReproRootLocator(rootOverride));
        registrar.RegisterInstance(typeof(CancellationToken), cts.Token);

        var app = new CommandApp(registrar);
        app.Configure(config =>
        {
            config.SetApplicationName("repro-runner");
            config.AddCommand<ListCommand>("list").WithDescription("List discovered repros and highlight invalid manifests.");
            config.AddCommand<ShowCommand>("show").WithDescription("Display manifest metadata for a repro.");
            config.AddCommand<ValidateCommand>("validate").WithDescription("Validate repro manifests.");
            config.AddCommand<RunCommand>("run").WithDescription("Execute repros against package and source builds.");
        });

        try
        {
            return await app.RunAsync(filteredArgs).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            console.MarkupLine("[yellow]Execution cancelled.[/]");
            return 1;
        }
        catch (CommandRuntimeException ex)
        {
            console.WriteException(ex, ExceptionFormats.ShortenEverything);
            return 1;
        }
        catch (Exception ex)
        {
            console.WriteException(ex, ExceptionFormats.ShortenEverything);
            return 1;
        }
    }

    private static (string[] FilteredArgs, string? RootOverride) ExtractGlobalOptions(string[] args)
    {
        if (args.Length == 0)
        {
            return (Array.Empty<string>(), null);
        }

        var filtered = new List<string>();
        string? rootOverride = null;
        var index = 0;

        while (index < args.Length)
        {
            var token = args[index];

            if (!token.StartsWith("-", StringComparison.Ordinal))
            {
                break;
            }

            if (token == "--root")
            {
                index++;
                if (index >= args.Length)
                {
                    throw new InvalidOperationException("--root requires a path.");
                }

                rootOverride = args[index];
                index++;
                continue;
            }

            if (token == "--")
            {
                filtered.Add(token);
                index++;
                break;
            }

            break;
        }

        for (; index < args.Length; index++)
        {
            filtered.Add(args[index]);
        }

        return (filtered.ToArray(), rootOverride);
    }
}
