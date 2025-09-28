using Spectre.Console;

namespace LiteDB.ReproRunner.Cli;

internal static class CliOutput
{
    public static void PrintInvalid(IAnsiConsole console, DiscoveredRepro repro)
    {
        console.MarkupLine($"[red]INVALID[/]  {Markup.Escape(NormalizePath(repro.RelativeManifestPath))}");
        foreach (var error in repro.Validation.Errors)
        {
            console.MarkupLine($"  - {Markup.Escape(error)}");
        }
    }

    public static void PrintManifest(IAnsiConsole console, DiscoveredRepro repro)
    {
        if (repro.Manifest is null)
        {
            return;
        }

        var manifest = repro.Manifest;
        var table = new Table().Border(TableBorder.Rounded).AddColumns("Field", "Value");
        table.AddRow("Id", Markup.Escape(manifest.Id));
        table.AddRow("Title", Markup.Escape(manifest.Title));
        table.AddRow("State", Markup.Escape(manifest.State));
        table.AddRow("TimeoutSeconds", Markup.Escape(manifest.TimeoutSeconds.ToString()));
        table.AddRow("RequiresParallel", Markup.Escape(manifest.RequiresParallel.ToString()));
        table.AddRow("DefaultInstances", Markup.Escape(manifest.DefaultInstances.ToString()));
        table.AddRow("SharedDatabaseKey", Markup.Escape(manifest.SharedDatabaseKey ?? "-"));
        table.AddRow("FailingSince", Markup.Escape(manifest.FailingSince ?? "-"));
        table.AddRow("Tags", Markup.Escape(manifest.Tags.Count > 0 ? string.Join(", ", manifest.Tags) : "-"));
        table.AddRow("Args", Markup.Escape(manifest.Args.Count > 0 ? string.Join(" ", manifest.Args) : "-"));

        if (manifest.Issues.Count > 0)
        {
            table.AddRow("Issues", Markup.Escape(string.Join(Environment.NewLine, manifest.Issues)));
        }

        console.Write(table);
    }

    public static void PrintValidationResult(IAnsiConsole console, DiscoveredRepro repro)
    {
        if (repro.IsValid)
        {
            console.MarkupLine($"[green]VALID[/]    {Markup.Escape(NormalizePath(repro.RelativeManifestPath))}");
        }
        else
        {
            PrintInvalid(console, repro);
        }
    }

    public static void PrintList(IAnsiConsole console, IReadOnlyList<DiscoveredRepro> valid)
    {
        if (valid.Count == 0)
        {
            console.MarkupLine("[yellow]No valid repro manifests found.[/]");
            return;
        }

        var table = new Table().Border(TableBorder.Rounded);
        table.AddColumns("Id", "State", "Timeout", "Failing Since", "Tags", "Title");

        foreach (var repro in valid)
        {
            if (repro.Manifest is null)
            {
                continue;
            }

            var manifest = repro.Manifest;
            table.AddRow(
                Markup.Escape(manifest.Id),
                Markup.Escape(manifest.State),
                Markup.Escape($"{manifest.TimeoutSeconds}s"),
                Markup.Escape(manifest.FailingSince ?? "-"),
                Markup.Escape(manifest.Tags.Count > 0 ? string.Join(",", manifest.Tags) : "-"),
                Markup.Escape(manifest.Title));
        }

        console.Write(table);
    }

    private static string NormalizePath(string path)
    {
        return path.Replace(Path.DirectorySeparatorChar, '/');
    }
}
