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

        var table = new Table().Border(TableBorder.Rounded).AddColumns("Repro", "Outcome", "Details");
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
                        table.AddRow(Markup.Escape(repro.RawId ?? "(unknown)"), "[red]Invalid manifest[/]", Markup.Escape(string.Join(Environment.NewLine, repro.Validation.Errors)));
                        ctx.Refresh();
                        overallExitCode = overallExitCode == 0 ? 2 : overallExitCode;
                        continue;
                    }

                    if (repro.Manifest is null)
                    {
                        table.AddRow(Markup.Escape(repro.RawId ?? "(unknown)"), "[red]Cannot run invalid manifest[/]", Markup.Escape("Validation failed and manifest not available."));
                        ctx.Refresh();
                        overallExitCode = overallExitCode == 0 ? 2 : overallExitCode;
                        continue;
                    }
                }

                if (repro.Manifest is null)
                {
                    table.AddRow(Markup.Escape(repro.RawId ?? "(unknown)"), "[red]Manifest missing[/]", Markup.Escape("Unable to load manifest."));
                    ctx.Refresh();
                    overallExitCode = overallExitCode == 0 ? 2 : overallExitCode;
                    continue;
                }

                var manifest = repro.Manifest;
                var instances = settings.Instances ?? manifest.DefaultInstances;

                if (manifest.RequiresParallel && instances < 2)
                {
                    table.AddRow(Markup.Escape(manifest.Id), "[red]Invalid options[/]", Markup.Escape("Requires at least 2 instances."));
                    ctx.Refresh();
                    overallExitCode = 1;
                    continue;
                }

                var timeoutSeconds = settings.Timeout ?? manifest.TimeoutSeconds;
                var packageVersion = TryResolvePackageVersion(repro.ProjectPath);
                var packageMessage = packageVersion is not null
                    ? $"Reproduces at version {packageVersion}."
                    : "Reproduces with NuGet package.";

                var runs = new[]
                {
                    new RunTarget(false, packageMessage),
                    new RunTarget(true, "Reproduces with current code.")
                };

                foreach (var run in runs)
                {
                    _cancellationToken.ThrowIfCancellationRequested();

                    var result = await _executor.ExecuteAsync(repro, run.UseProjectReference, instances, timeoutSeconds, _cancellationToken).ConfigureAwait(false);
                    var status = result.Reproduced
                        ? $"{run.Message} [green]✅[/]"
                        : $"{run.Message} [red]❌[/]";
                    var details = result.Reproduced
                        ? $"Duration: {FormatDuration(result.Duration)}"
                        : $"Exit code {result.ExitCode}, Duration: {FormatDuration(result.Duration)}";

                    table.AddRow(Markup.Escape(manifest.Id), status, Markup.Escape(details));
                    ctx.Refresh();

                    if (!result.Reproduced)
                    {
                        overallExitCode = overallExitCode == 0 ? result.ExitCode : overallExitCode;
                    }
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

    private readonly record struct RunTarget(bool UseProjectReference, string Message);
}
