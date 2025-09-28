using System.ComponentModel;
using Spectre.Console;
using Spectre.Console.Cli;

namespace LiteDB.ReproRunner.Cli.Commands;

internal sealed class RunCommandSettings : RootCommandSettings
{
    [CommandArgument(0, "[id]")]
    [Description("Identifier of the repro to run.")]
    public string? Id { get; set; }

    [CommandOption("--all")]
    [Description("Run every repro in sequence.")]
    public bool All { get; set; }

    [CommandOption("--instances <N>")]
    [Description("Override the number of instances to launch.")]
    public int? Instances { get; set; }

    [CommandOption("--timeout <SECONDS>")]
    [Description("Override the timeout applied to each repro.")]
    public int? Timeout { get; set; }

    [CommandOption("--skipValidation")]
    [Description("Allow execution even when manifest validation fails.")]
    public bool SkipValidation { get; set; }

    public override ValidationResult Validate()
    {
        if (All && Id is not null)
        {
            return ValidationResult.Error("Cannot specify both --all and <id>.");
        }

        if (!All && Id is null)
        {
            return ValidationResult.Error("run requires a repro id or --all.");
        }

        if (Instances is int instances && instances < 1)
        {
            return ValidationResult.Error("--instances expects a positive integer.");
        }

        if (Timeout is int timeout && timeout < 1)
        {
            return ValidationResult.Error("--timeout expects a positive integer value in seconds.");
        }

        return base.Validate();
    }
}
