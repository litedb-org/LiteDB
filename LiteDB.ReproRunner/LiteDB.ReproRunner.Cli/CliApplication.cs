using System.Linq;
using System.Xml.Linq;
using Spectre.Console;

namespace LiteDB.ReproRunner.Cli;

internal sealed class CliApplication
{
    private readonly IAnsiConsole _console;
    private readonly ReproExecutor _executor;

    public CliApplication(IAnsiConsole console, ReproExecutor executor)
    {
        _console = console ?? throw new ArgumentNullException(nameof(console));
        _executor = executor ?? throw new ArgumentNullException(nameof(executor));
    }

    public async Task<int> RunAsync(string[] args, CancellationToken cancellationToken)
    {
        try
        {
            var (rootOverride, command, commandArgs, showHelp) = ParseGlobalOptions(args);

            if (showHelp)
            {
                PrintUsage();
                return 0;
            }

            if (command is null)
            {
                PrintUsage();
                return 1;
            }

            var root = ResolveRoot(rootOverride);
            var repository = new ManifestRepository(root);

            return command switch
            {
                "list" => RunList(repository, commandArgs),
                "show" => RunShow(repository, commandArgs),
                "validate" => RunValidate(repository, commandArgs),
                "run" => await RunReprosAsync(repository, commandArgs, cancellationToken).ConfigureAwait(false),
                _ => UnknownCommand(command)
            };
        }
        catch (OperationCanceledException)
        {
            _console.MarkupLine("[yellow]Execution cancelled.[/]");
            return 1;
        }
        catch (CliUsageException ex)
        {
            _console.WriteException(ex, ExceptionFormats.ShortenEverything);
            return 1;
        }
        catch (InvalidOperationException ex)
        {
            _console.MarkupLine($"[red]{Markup.Escape(ex.Message)}[/]");
            return 1;
        }
    }

    private static (string? RootOverride, string? Command, string[] CommandArgs, bool ShowHelp) ParseGlobalOptions(string[] args)
    {
        string? rootOverride = null;
        bool showHelp = false;
        var index = 0;

        while (index < args.Length)
        {
            var token = args[index];

            if (token == "--")
            {
                index++;
                break;
            }

            if (token == "--root")
            {
                index++;
                if (index >= args.Length)
                {
                    throw new CliUsageException("--root requires a path.");
                }

                rootOverride = args[index];
                index++;
                continue;
            }

            if (token is "--help" or "-h")
            {
                showHelp = true;
                index++;
                continue;
            }

            break;
        }

        if (index >= args.Length)
        {
            return (rootOverride, null, Array.Empty<string>(), showHelp);
        }

        var command = args[index];
        index++;

        var commandArgs = new string[args.Length - index];
        Array.Copy(args, index, commandArgs, 0, commandArgs.Length);

        return (rootOverride, command, commandArgs, showHelp);
    }

    private void PrintUsage()
    {
        var panel = new Panel("Usage: repro-runner [--root <path>] <command> [options]")
            .Expand();
        _console.Write(panel);
        _console.MarkupLine("Commands:");
        _console.MarkupLine("  [yellow]list[/] [--strict]                     List discovered repros and highlight invalid manifests.");
        _console.MarkupLine("  [yellow]show[/] <id>                           Display the manifest metadata for a repro.");
        _console.MarkupLine("  [yellow]validate[/] [--all|--id <id>]          Validate manifest files (exit 2 on invalid).");
        _console.MarkupLine("  [yellow]run[/] [--all|<id>] [options]          Execute repros against package and source builds.");
        _console.WriteLine();
        _console.MarkupLine("Global options:");
        _console.MarkupLine("  --root <path>                                 Override the LiteDB.ReproRunner root directory.");
        _console.MarkupLine("  --help                                        Show this usage information.");
    }

    private string ResolveRoot(string? rootOverride)
    {
        if (!string.IsNullOrEmpty(rootOverride))
        {
            var candidate = Path.GetFullPath(rootOverride);
            var resolved = TryResolveRoot(candidate);

            if (resolved is null)
            {
                throw new InvalidOperationException($"Unable to locate a Repros directory under '{candidate}'.");
            }

            return resolved;
        }

        var searchRoots = new List<DirectoryInfo>
        {
            new DirectoryInfo(Directory.GetCurrentDirectory())
        };

        var baseDirectory = new DirectoryInfo(AppContext.BaseDirectory);
        if (!searchRoots.Any(d => string.Equals(d.FullName, baseDirectory.FullName, StringComparison.Ordinal)))
        {
            searchRoots.Add(baseDirectory);
        }

        foreach (var start in searchRoots)
        {
            var current = start;

            while (current is not null)
            {
                var resolved = TryResolveRoot(current.FullName);
                if (resolved is not null)
                {
                    return resolved;
                }

                current = current.Parent;
            }
        }

        throw new InvalidOperationException("Unable to locate the LiteDB.ReproRunner directory. Use --root to specify the path.");
    }

    private static string? TryResolveRoot(string path)
    {
        if (Directory.Exists(Path.Combine(path, "Repros")))
        {
            return Path.GetFullPath(path);
        }

        var candidate = Path.Combine(path, "LiteDB.ReproRunner");
        if (Directory.Exists(Path.Combine(candidate, "Repros")))
        {
            return Path.GetFullPath(candidate);
        }

        return null;
    }

    private int RunList(ManifestRepository repository, string[] args)
    {
        var strict = false;

        foreach (var token in args)
        {
            if (token == "--strict")
            {
                strict = true;
            }
            else
            {
                throw new CliUsageException($"list: unknown option '{token}'.");
            }
        }

        var manifests = repository.Discover();
        var valid = manifests.Where(x => x.IsValid).ToList();
        var invalid = manifests.Where(x => !x.IsValid).ToList();

        if (valid.Count > 0)
        {
            var table = new Table().Border(TableBorder.Rounded);
            table.AddColumns("Id", "State", "Timeout", "Failing Since", "Tags", "Title");

            foreach (var repro in valid)
            {
                var manifest = repro.Manifest!;
                table.AddRow(
                    Markup.Escape(manifest.Id),
                    Markup.Escape(manifest.State),
                    Markup.Escape($"{manifest.TimeoutSeconds}s"),
                    Markup.Escape(manifest.FailingSince ?? "-"),
                    Markup.Escape(manifest.Tags.Count > 0 ? string.Join(",", manifest.Tags) : "-"),
                    Markup.Escape(manifest.Title));
            }

            _console.Write(table);
        }
        else
        {
            _console.MarkupLine("[yellow]No valid repro manifests found.[/]");
        }

        foreach (var repro in invalid)
        {
            PrintInvalid(repro);
        }

        if (strict && invalid.Count > 0)
        {
            return 2;
        }

        return 0;
    }

    private int RunShow(ManifestRepository repository, string[] args)
    {
        if (args.Length == 0)
        {
            throw new CliUsageException("show requires a repro id.");
        }

        if (args.Length > 1)
        {
            throw new CliUsageException("show accepts only the repro id.");
        }

        var id = args[0];
        var manifests = repository.Discover();
        var repro = manifests.FirstOrDefault(x => string.Equals(x.Manifest?.Id ?? x.RawId, id, StringComparison.OrdinalIgnoreCase));

        if (repro is null)
        {
            _console.MarkupLine($"[red]Repro '{Markup.Escape(id)}' was not found.[/]");
            return 1;
        }

        if (!repro.IsValid)
        {
            PrintInvalid(repro);
            return 2;
        }

        PrintManifest(repro);
        return 0;
    }

    private int RunValidate(ManifestRepository repository, string[] args)
    {
        bool validateAll = true;
        string? targetId = null;

        var reader = new ArgumentReader(args);

        while (reader.TryRead(out var token))
        {
            if (token == "--all")
            {
                validateAll = true;
                targetId = null;
            }
            else if (token == "--id")
            {
                targetId = reader.ReadValue("--id");
                validateAll = false;
            }
            else
            {
                throw new CliUsageException($"validate: unknown option '{token}'.");
            }
        }

        var manifests = repository.Discover();

        if (!validateAll)
        {
            var repro = manifests.FirstOrDefault(x => string.Equals(x.Manifest?.Id ?? x.RawId, targetId, StringComparison.OrdinalIgnoreCase));

            if (repro is null)
            {
                _console.MarkupLine($"[red]Repro '{Markup.Escape(targetId!)}' was not found.[/]");
                return 1;
            }

            PrintValidationResult(repro);
            return repro.IsValid ? 0 : 2;
        }

        var anyInvalid = false;

        foreach (var repro in manifests)
        {
            PrintValidationResult(repro);
            anyInvalid |= !repro.IsValid;
        }

        return anyInvalid ? 2 : 0;
    }

    private async Task<int> RunReprosAsync(ManifestRepository repository, string[] args, CancellationToken cancellationToken)
    {
        var reader = new ArgumentReader(args);
        string? targetId = null;
        var runAll = false;
        int? overrideInstances = null;
        int? overrideTimeout = null;
        var skipValidation = false;

        while (reader.TryRead(out var token))
        {
            switch (token)
            {
                case "--all":
                    runAll = true;
                    break;
                case "--instances":
                    var instancesValue = reader.ReadValue("--instances");
                    if (!int.TryParse(instancesValue, out var instances) || instances < 1)
                    {
                        throw new CliUsageException("--instances expects a positive integer.");
                    }

                    overrideInstances = instances;
                    break;
                case "--timeout":
                    var timeoutValue = reader.ReadValue("--timeout");
                    if (!int.TryParse(timeoutValue, out var timeout) || timeout < 1)
                    {
                        throw new CliUsageException("--timeout expects a positive integer value in seconds.");
                    }

                    overrideTimeout = timeout;
                    break;
                case "--skipValidation":
                    skipValidation = true;
                    break;
                default:
                    if (targetId is not null)
                    {
                        throw new CliUsageException("run accepts only one repro id.");
                    }

                    targetId = token;
                    break;
            }
        }

        if (!runAll && targetId is null)
        {
            throw new CliUsageException("run requires a repro id or --all.");
        }

        if (runAll && targetId is not null)
        {
            throw new CliUsageException("run cannot specify both --all and a repro id.");
        }

        var manifests = repository.Discover();
        var selected = runAll
            ? manifests.ToList()
            : manifests.Where(x => string.Equals(x.Manifest?.Id ?? x.RawId, targetId, StringComparison.OrdinalIgnoreCase)).ToList();

        if (selected.Count == 0)
        {
            if (runAll)
            {
                _console.MarkupLine("[yellow]No repros discovered.[/]");
                return 0;
            }

            _console.MarkupLine($"[red]Repro '{Markup.Escape(targetId!)}' was not found.[/]");
            return 1;
        }

        var table = new Table().Border(TableBorder.Rounded).AddColumns("Repro", "Outcome", "Details");
        var overallExitCode = 0;

        await _console.Live(table).StartAsync(async ctx =>
        {
            foreach (var repro in selected)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (!repro.IsValid)
                {
                    if (!skipValidation)
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
                var instances = overrideInstances ?? manifest.DefaultInstances;

                if (manifest.RequiresParallel && instances < 2)
                {
                    table.AddRow(Markup.Escape(manifest.Id), "[red]Invalid options[/]", Markup.Escape("Requires at least 2 instances."));
                    ctx.Refresh();
                    overallExitCode = 1;
                    continue;
                }

                var timeoutSeconds = overrideTimeout ?? manifest.TimeoutSeconds;
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
                    cancellationToken.ThrowIfCancellationRequested();

                    var result = await _executor.ExecuteAsync(repro, run.UseProjectReference, instances, timeoutSeconds, cancellationToken).ConfigureAwait(false);
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

    private int UnknownCommand(string command)
    {
        _console.MarkupLine($"[red]Unknown command '{Markup.Escape(command)}'.[/]");
        PrintUsage();
        return 1;
    }

    private void PrintInvalid(DiscoveredRepro repro)
    {
        _console.MarkupLine($"[red]INVALID[/]  {Markup.Escape(repro.RelativeManifestPath.Replace(Path.DirectorySeparatorChar, '/'))}");
        foreach (var error in repro.Validation.Errors)
        {
            _console.MarkupLine($"  - {Markup.Escape(error)}");
        }
    }

    private void PrintManifest(DiscoveredRepro repro)
    {
        var manifest = repro.Manifest!;
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

        _console.Write(table);
    }

    private void PrintValidationResult(DiscoveredRepro repro)
    {
        if (repro.IsValid)
        {
            _console.MarkupLine($"[green]VALID[/]    {Markup.Escape(repro.RelativeManifestPath.Replace(Path.DirectorySeparatorChar, '/'))}");
        }
        else
        {
            PrintInvalid(repro);
        }
    }

    private readonly record struct RunTarget(bool UseProjectReference, string Message);
}
