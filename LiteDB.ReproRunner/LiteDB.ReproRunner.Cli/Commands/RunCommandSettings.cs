using System.ComponentModel;
using Spectre.Console;
using Spectre.Console.Cli;

namespace LiteDB.ReproRunner.Cli.Commands;

internal sealed class RunCommandSettings : RootCommandSettings
{
    /// <summary>
    /// Gets the default frames-per-second limit applied to the live UI.
    /// </summary>
    public const decimal DefaultFps = 30m;

    /// <summary>
    /// Gets or sets the identifier of the repro to execute.
    /// </summary>
    [CommandArgument(0, "[id]")]
    [Description("Identifier of the repro to run.")]
    public string? Id { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether all repros should be executed.
    /// </summary>
    [CommandOption("--all")]
    [Description("Run every repro in sequence.")]
    public bool All { get; set; }

    /// <summary>
    /// Gets or sets the number of instances to launch for each repro.
    /// </summary>
    [CommandOption("--instances <N>")]
    [Description("Override the number of instances to launch.")]
    public int? Instances { get; set; }

    /// <summary>
    /// Gets or sets the timeout to apply to each repro execution, in seconds.
    /// </summary>
    [CommandOption("--timeout <SECONDS>")]
    [Description("Override the timeout applied to each repro.")]
    public int? Timeout { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether validation failures should be ignored.
    /// </summary>
    [CommandOption("--skipValidation")]
    [Description("Allow execution even when manifest validation fails.")]
    public bool SkipValidation { get; set; }

    /// <summary>
    /// Gets or sets the maximum number of times the live UI refreshes per second.
    /// </summary>
    [CommandOption("--fps <FPS>")]
    [Description("Limit the live UI refresh rate in frames per second (default: 30).")]
    public decimal? Fps { get; set; }

    /// <summary>
    /// Validates the run command settings.
    /// </summary>
    /// <returns>The validation result describing any errors.</returns>
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

        if (Fps is decimal fps && fps <= 0)
        {
            return ValidationResult.Error("--fps expects a positive decimal value.");
        }

        return base.Validate();
    }
}
