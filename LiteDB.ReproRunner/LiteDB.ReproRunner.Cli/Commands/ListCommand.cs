using LiteDB.ReproRunner.Cli.Infrastructure;
using LiteDB.ReproRunner.Cli.Manifests;
using Spectre.Console;
using Spectre.Console.Cli;

namespace LiteDB.ReproRunner.Cli.Commands;

internal sealed class ListCommand : Command<ListCommandSettings>
{
    private readonly IAnsiConsole _console;
    private readonly ReproRootLocator _rootLocator;

    /// <summary>
    /// Initializes a new instance of the <see cref="ListCommand"/> class.
    /// </summary>
    /// <param name="console">The console used to render output.</param>
    /// <param name="rootLocator">Resolves the repro root directory.</param>
    public ListCommand(IAnsiConsole console, ReproRootLocator rootLocator)
    {
        _console = console ?? throw new ArgumentNullException(nameof(console));
        _rootLocator = rootLocator ?? throw new ArgumentNullException(nameof(rootLocator));
    }

    /// <summary>
    /// Executes the list command.
    /// </summary>
    /// <param name="context">The Spectre command context.</param>
    /// <param name="settings">The user-provided settings.</param>
    /// <returns>The process exit code.</returns>
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
