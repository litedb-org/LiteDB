using System.ComponentModel;
using Spectre.Console.Cli;

namespace LiteDB.ReproRunner.Cli.Commands;

internal class RootCommandSettings : CommandSettings
{
    /// <summary>
    /// Gets or sets the root directory that contains repro manifests.
    /// </summary>
    [CommandOption("--root <PATH>")]
    [Description("Override the LiteDB.ReproRunner root directory.")]
    public string? Root { get; set; }
}
