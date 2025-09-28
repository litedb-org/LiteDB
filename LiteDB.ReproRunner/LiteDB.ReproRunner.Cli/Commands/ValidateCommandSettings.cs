using System.ComponentModel;
using Spectre.Console;
using Spectre.Console.Cli;

namespace LiteDB.ReproRunner.Cli.Commands;

internal sealed class ValidateCommandSettings : RootCommandSettings
{
    [CommandOption("--all")]
    [Description("Validate every repro manifest (default).")]
    public bool All { get; set; }

    [CommandOption("--id <ID>")]
    [Description("Validate a single repro by id.")]
    public string? Id { get; set; }

    public override ValidationResult Validate()
    {
        if (All && Id is not null)
        {
            return ValidationResult.Error("Cannot specify both --all and --id.");
        }

        return base.Validate();
    }
}
