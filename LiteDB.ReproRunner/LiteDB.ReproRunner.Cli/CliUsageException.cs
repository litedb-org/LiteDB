namespace LiteDB.ReproRunner.Cli;

internal sealed class CliUsageException : Exception
{
    public CliUsageException(string message)
        : base(message)
    {
    }
}
