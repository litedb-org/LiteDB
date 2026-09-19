using System.ComponentModel;
using Spectre.Console;
using Spectre.Console.Cli;

namespace LiteDB.ReproRunner.Cli.Commands;

internal sealed class ValidateCommandSettings : RootCommandSettings
{
    /// <summary>
    /// Gets or sets a value indicating whether all manifests should be validated.
    /// </summary>
    [CommandOption("--all")]
    [Description("Validate every repro manifest (default).")]
    public bool All { get; set; }

    /// <summary>
    /// Gets or sets the identifier of the repro to validate.
    /// </summary>
    [CommandOption("--id <ID>")]
    [Description("Validate a single repro by id.")]
    public string? Id { get; set; }

    /// <summary>
    /// Validates the command settings.
    /// </summary>
    /// <returns>The validation result describing any errors.</returns>
    public override ValidationResult Validate()
    {
        if (All && Id is not null)
        {
            return ValidationResult.Error("Cannot specify both --all and --id.");
        }

        return base.Validate();
    }
}
