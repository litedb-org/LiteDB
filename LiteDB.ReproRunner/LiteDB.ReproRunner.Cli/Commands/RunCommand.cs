using System.Threading;
using System.Xml.Linq;
using Spectre.Console;
using Spectre.Console.Cli;

namespace LiteDB.ReproRunner.Cli.Commands;

internal sealed class RunCommand : AsyncCommand<RunCommandSettings>
{
    private readonly IAnsiConsole _console;
    private readonly ReproRootLocator _rootLocator;
    private readonly ReproExecutor _executor;
    private readonly CancellationToken _cancellationToken;

    public RunCommand(IAnsiConsole console, ReproRootLocator rootLocator, ReproExecutor executor, CancellationToken cancellationToken)
    {
        _console = console ?? throw new ArgumentNullException(nameof(console));
        _rootLocator = rootLocator ?? throw new ArgumentNullException(nameof(rootLocator));
        _executor = executor ?? throw new ArgumentNullException(nameof(executor));
        _cancellationToken = cancellationToken;
    }

    public override async Task<int> ExecuteAsync(CommandContext context, RunCommandSettings settings)
    {
        _cancellationToken.ThrowIfCancellationRequested();
        var repository = new ManifestRepository(_rootLocator.ResolveRoot(settings.Root));
        var manifests = repository.Discover();
        var selected = settings.All
            ? manifests.ToList()
            : manifests.Where(x => string.Equals(x.Manifest?.Id ?? x.RawId, settings.Id, StringComparison.OrdinalIgnoreCase)).ToList();

        if (selected.Count == 0)
        {
            if (settings.All)
            {
                _console.MarkupLine("[yellow]No repros discovered.[/]");
                return 0;
            }

            _console.MarkupLine($"[red]Repro '{Markup.Escape(settings.Id!)}' was not found.[/]");
            return 1;
        }

        var table = new Table().Border(TableBorder.Rounded).AddColumns("Repro", "Repro Version", "Reproduced", "Fixed");
        var overallExitCode = 0;

        await _console.Live(table).StartAsync(async ctx =>
        {
            foreach (var repro in selected)
            {
                _cancellationToken.ThrowIfCancellationRequested();

                if (!repro.IsValid)
                {
                    if (!settings.SkipValidation)
                    {
                        table.AddRow(Markup.Escape(repro.RawId ?? "(unknown)"), "[red]Invalid[/]", "[red]❌[/]", "[red]❌[/]");
                        ctx.Refresh();
                        overallExitCode = overallExitCode == 0 ? 2 : overallExitCode;
                        continue;
                    }

                    if (repro.Manifest is null)
                    {
                        table.AddRow(Markup.Escape(repro.RawId ?? "(unknown)"), "[red]Invalid[/]", "[red]❌[/]", "[red]❌[/]");
                        ctx.Refresh();
                        overallExitCode = overallExitCode == 0 ? 2 : overallExitCode;
                        continue;
                    }
                }

                if (repro.Manifest is null)
                {
                    table.AddRow(Markup.Escape(repro.RawId ?? "(unknown)"), "[red]Missing[/]", "[red]❌[/]", "[red]❌[/]");
                    ctx.Refresh();
                    overallExitCode = overallExitCode == 0 ? 2 : overallExitCode;
                    continue;
                }

                var manifest = repro.Manifest;
                var instances = settings.Instances ?? manifest.DefaultInstances;

                if (manifest.RequiresParallel && instances < 2)
                {
                    table.AddRow(Markup.Escape(manifest.Id), "[red]Config Error[/]", "[red]❌[/]", "[red]❌[/]");
                    ctx.Refresh();
                    overallExitCode = 1;
                    continue;
                }

                var timeoutSeconds = settings.Timeout ?? manifest.TimeoutSeconds;
                var packageVersion = TryResolvePackageVersion(repro.ProjectPath);
                var displayVersion = packageVersion ?? "NuGet";

                _cancellationToken.ThrowIfCancellationRequested();

                // Test with package version first
                var packageResult = await _executor.ExecuteAsync(repro, false, instances, timeoutSeconds, _cancellationToken).ConfigureAwait(false);
                
                // Test with current code
                var currentResult = await _executor.ExecuteAsync(repro, true, instances, timeoutSeconds, _cancellationToken).ConfigureAwait(false);

                var reproducedStatus = packageResult.Reproduced ? "[green]✅[/]" : "[red]❌[/]";
                var fixedStatus = currentResult.Reproduced 
                    ? "[red]❌[/]"  // Still reproduces (crossed out X)
                    : "[green]✅[/]";   // Fixed (check mark)

                table.AddRow(Markup.Escape(manifest.Id), Markup.Escape(displayVersion), reproducedStatus, fixedStatus);
                ctx.Refresh();

                if (packageResult.Reproduced || currentResult.Reproduced)
                {
                    overallExitCode = overallExitCode == 0 ? 1 : overallExitCode;
                }
            }
        }).ConfigureAwait(false);

        return overallExitCode;
    }

    private static string FormatDuration(TimeSpan duration)
    {
        return duration.TotalSeconds >= 10
            ? duration.ToString(@"hh\:mm\:ss")
            : $"{duration.TotalSeconds:0.###}s";
    }

    private static string? TryResolvePackageVersion(string? projectPath)
    {
        if (projectPath is null || !File.Exists(projectPath))
        {
            return null;
        }

        try
        {
            var document = XDocument.Load(projectPath);
            var ns = document.Root?.Name.Namespace ?? XNamespace.None;

            var versionElement = document
                .Descendants(ns + "LiteDBPackageVersion")
                .FirstOrDefault();

            if (versionElement is not null && !string.IsNullOrWhiteSpace(versionElement.Value))
            {
                return versionElement.Value.Trim();
            }

            var packageReference = document
                .Descendants(ns + "PackageReference")
                .FirstOrDefault(e => string.Equals(e.Attribute("Include")?.Value, "LiteDB", StringComparison.OrdinalIgnoreCase));

            var version = packageReference?.Attribute("Version")?.Value;
            if (!string.IsNullOrWhiteSpace(version))
            {
                return version!.Trim();
            }
        }
        catch
        {
        }

        return null;
    }
}
