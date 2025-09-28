namespace LiteDB.ReproRunner.Cli;

internal interface IConsole
{
    TextWriter Out { get; }
    TextWriter Error { get; }
}

internal sealed class ConsoleAdapter : IConsole
{
    public TextWriter Out => Console.Out;

    public TextWriter Error => Console.Error;
}
