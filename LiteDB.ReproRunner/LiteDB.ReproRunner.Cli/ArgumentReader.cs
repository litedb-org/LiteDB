namespace LiteDB.ReproRunner.Cli;

internal sealed class ArgumentReader
{
    private readonly string[] _args;
    private int _index;

    public ArgumentReader(string[] args)
    {
        _args = args ?? Array.Empty<string>();
        _index = 0;
    }

    public bool TryRead(out string value)
    {
        if (_index < _args.Length)
        {
            value = _args[_index++];
            return true;
        }

        value = string.Empty;
        return false;
    }

    public string ReadValue(string optionName)
    {
        if (!TryRead(out var value))
        {
            throw new CliUsageException($"{optionName} requires a value.");
        }

        return value;
    }
}
