namespace LiteDB.ReproRunner.Cli;

internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        var app = new CliApplication(new ConsoleAdapter());
        return await app.RunAsync(args).ConfigureAwait(false);
    }
}
