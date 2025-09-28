using Spectre.Console;
using Spectre.Console.Cli;

namespace LiteDB.ReproRunner.Cli.Commands;

internal sealed class ListCommand : Command<ListCommandSettings>
{
    private readonly IAnsiConsole _console;
    private readonly ReproRootLocator _rootLocator;

    public ListCommand(IAnsiConsole console, ReproRootLocator rootLocator)
    {
        _console = console ?? throw new ArgumentNullException(nameof(console));
        _rootLocator = rootLocator ?? throw new ArgumentNullException(nameof(rootLocator));
    }

    public override int Execute(CommandContext context, ListCommandSettings settings)
    {
        var repository = new ManifestRepository(_rootLocator.ResolveRoot(settings.Root));
        var manifests = repository.Discover();
        var valid = manifests.Where(x => x.IsValid).ToList();
        var invalid = manifests.Where(x => !x.IsValid).ToList();

        CliOutput.PrintList(_console, valid);

        foreach (var repro in invalid)
        {
            CliOutput.PrintInvalid(_console, repro);
        }

        if (settings.Strict && invalid.Count > 0)
        {
            return 2;
        }

        return 0;
    }
}
