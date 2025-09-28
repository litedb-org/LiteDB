using LiteDB.ReproRunner.Cli.Infrastructure;
using LiteDB.ReproRunner.Cli.Manifests;
using Spectre.Console;
using Spectre.Console.Cli;

namespace LiteDB.ReproRunner.Cli.Commands;

internal sealed class ShowCommand : Command<ShowCommandSettings>
{
    private readonly IAnsiConsole _console;
    private readonly ReproRootLocator _rootLocator;

    /// <summary>
    /// Initializes a new instance of the <see cref="ShowCommand"/> class.
    /// </summary>
    /// <param name="console">The console used to render output.</param>
    /// <param name="rootLocator">Resolves the repro root directory.</param>
    public ShowCommand(IAnsiConsole console, ReproRootLocator rootLocator)
    {
        _console = console ?? throw new ArgumentNullException(nameof(console));
        _rootLocator = rootLocator ?? throw new ArgumentNullException(nameof(rootLocator));
    }

    /// <summary>
    /// Executes the show command.
    /// </summary>
    /// <param name="context">The Spectre command context.</param>
    /// <param name="settings">The user-provided settings.</param>
    /// <returns>The process exit code.</returns>
    public override int Execute(CommandContext context, ShowCommandSettings settings)
    {
        var repository = new ManifestRepository(_rootLocator.ResolveRoot(settings.Root));
        var manifests = repository.Discover();
        var repro = manifests.FirstOrDefault(x => string.Equals(x.Manifest?.Id ?? x.RawId, settings.Id, StringComparison.OrdinalIgnoreCase));

        if (repro is null)
        {
            _console.MarkupLine($"[red]Repro '{Markup.Escape(settings.Id)}' was not found.[/]");
            return 1;
        }

        if (!repro.IsValid)
        {
            CliOutput.PrintInvalid(_console, repro);
            return 2;
        }

        CliOutput.PrintManifest(_console, repro);
        return 0;
    }
}
