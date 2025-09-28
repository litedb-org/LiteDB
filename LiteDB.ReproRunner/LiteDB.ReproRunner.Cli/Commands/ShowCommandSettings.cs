using System.ComponentModel;
using Spectre.Console.Cli;

namespace LiteDB.ReproRunner.Cli.Commands;

internal sealed class ShowCommandSettings : RootCommandSettings
{
    [CommandArgument(0, "<id>")]
    [Description("Identifier of the repro to show.")]
    public string Id { get; set; } = string.Empty;
}
