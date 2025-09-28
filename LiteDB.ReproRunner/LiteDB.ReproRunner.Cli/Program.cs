using Spectre.Console;

namespace LiteDB.ReproRunner.Cli;

internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        using var cts = new CancellationTokenSource();

        Console.CancelKeyPress += (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            cts.Cancel();
        };

        var console = AnsiConsole.Create(new AnsiConsoleSettings());
        var app = new CliApplication(console, new ReproExecutor());
        return await app.RunAsync(args, cts.Token).ConfigureAwait(false);
    }
}
