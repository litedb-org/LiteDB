using System.ComponentModel;
using Spectre.Console.Cli;

namespace LiteDB.ReproRunner.Cli.Commands;

internal sealed class ListCommandSettings : RootCommandSettings
{
    /// <summary>
    /// Gets or sets a value indicating whether the command should fail when invalid manifests exist.
    /// </summary>
    [CommandOption("--strict")]
    [Description("Return exit code 2 if any manifests are invalid.")]
    public bool Strict { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether output should be emitted as JSON.
    /// </summary>
    [CommandOption("--json")]
    [Description("Emit the repro inventory as JSON instead of a rendered table.")]
    public bool Json { get; set; }

    /// <summary>
    /// Gets or sets an optional regular expression used to filter repro identifiers.
    /// </summary>
    [CommandOption("--filter <REGEX>")]
    [Description("Return only repros whose identifiers match the supplied regular expression.")]
    public string? Filter { get; set; }
}
