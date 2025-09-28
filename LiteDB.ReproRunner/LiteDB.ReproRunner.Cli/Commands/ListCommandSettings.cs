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
}
