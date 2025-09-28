using Spectre.Console;
using Spectre.Console.Cli;

namespace LiteDB.ReproRunner.Cli.Commands;

internal sealed class ValidateCommand : Command<ValidateCommandSettings>
{
    private readonly IAnsiConsole _console;
    private readonly ReproRootLocator _rootLocator;

    public ValidateCommand(IAnsiConsole console, ReproRootLocator rootLocator)
    {
        _console = console ?? throw new ArgumentNullException(nameof(console));
        _rootLocator = rootLocator ?? throw new ArgumentNullException(nameof(rootLocator));
    }

    public override int Execute(CommandContext context, ValidateCommandSettings settings)
    {
        var repository = new ManifestRepository(_rootLocator.ResolveRoot(settings.Root));
        var manifests = repository.Discover();

        if (!settings.All && settings.Id is not null)
        {
            var repro = manifests.FirstOrDefault(x => string.Equals(x.Manifest?.Id ?? x.RawId, settings.Id, StringComparison.OrdinalIgnoreCase));
            if (repro is null)
            {
                _console.MarkupLine($"[red]Repro '{Markup.Escape(settings.Id)}' was not found.[/]");
                return 1;
            }

            CliOutput.PrintValidationResult(_console, repro);
            return repro.IsValid ? 0 : 2;
        }

        var anyInvalid = false;
        foreach (var repro in manifests)
        {
            CliOutput.PrintValidationResult(_console, repro);
            anyInvalid |= !repro.IsValid;
        }

        return anyInvalid ? 2 : 0;
    }
}
