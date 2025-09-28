using System.IO;
using System.Linq;
using System.Threading;

namespace LiteDB.ReproRunner.Cli;

internal sealed class CliApplication
{
    private readonly IConsole _console;

    public CliApplication(IConsole console)
    {
        _console = console;
    }

    public async Task<int> RunAsync(string[] args)
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
                "run" => await RunReproAsync(repository, commandArgs).ConfigureAwait(false),
                _ => UnknownCommand(command)
            };
        }
        catch (CliUsageException ex)
        {
            _console.Error.WriteLine(ex.Message);
            return 1;
        }
        catch (InvalidOperationException ex)
        {
            _console.Error.WriteLine(ex.Message);
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
        _console.Out.WriteLine("Usage: repro-runner [--root <path>] <command> [options]");
        _console.Out.WriteLine();
        _console.Out.WriteLine("Commands:");
        _console.Out.WriteLine("  list [--strict]                 List discovered repros and highlight invalid manifests.");
        _console.Out.WriteLine("  show <id>                       Display the manifest metadata for a repro.");
        _console.Out.WriteLine("  validate [--all|--id <id>]      Validate manifest files (exit 2 on invalid).");
        _console.Out.WriteLine("  run <id> [options]              Execute a repro project.");
        _console.Out.WriteLine();
        _console.Out.WriteLine("Global options:");
        _console.Out.WriteLine("  --root <path>                   Override the LiteDB.ReproRunner root directory.");
        _console.Out.WriteLine("  --help                          Show this usage information.");
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
            PrintListHeader();
            foreach (var repro in valid)
            {
                PrintListRow(repro.Manifest!);
            }
        }
        else
        {
            _console.Out.WriteLine("No valid repro manifests found.");
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
            _console.Error.WriteLine($"Repro '{id}' was not found.");
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
                _console.Error.WriteLine($"Repro '{targetId}' was not found.");
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

    private async Task<int> RunReproAsync(ManifestRepository repository, string[] args)
    {
        if (args.Length == 0)
        {
            throw new CliUsageException("run requires a repro id.");
        }

        var reader = new ArgumentReader(args);

        if (!reader.TryRead(out var id))
        {
            throw new CliUsageException("run requires a repro id.");
        }
        bool? useProjectReference = null;
        int? overrideInstances = null;
        int? overrideTimeout = null;
        var skipValidation = false;

        while (reader.TryRead(out var token))
        {
            switch (token)
            {
                case "--useProjectRef":
                    useProjectReference = true;
                    break;
                case "--usePackage":
                    useProjectReference = false;
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
                    throw new CliUsageException($"run: unknown option '{token}'.");
            }
        }

        var manifests = repository.Discover();
        var repro = manifests.FirstOrDefault(x => string.Equals(x.Manifest?.Id ?? x.RawId, id, StringComparison.OrdinalIgnoreCase));

        if (repro is null)
        {
            _console.Error.WriteLine($"Repro '{id}' was not found.");
            return 1;
        }

        if (!skipValidation && !repro.IsValid)
        {
            PrintInvalid(repro);
            return 2;
        }

        if (repro.Manifest is null)
        {
            _console.Error.WriteLine($"Repro '{id}' could not be loaded.");
            return 2;
        }

        if (useProjectReference is null)
        {
            useProjectReference = false;
        }

        var instancesToRun = overrideInstances ?? repro.Manifest.DefaultInstances;

        if (repro.Manifest.RequiresParallel && instancesToRun < 2)
        {
            throw new CliUsageException($"{repro.Manifest.Id} requires at least 2 instances.");
        }

        var timeoutSeconds = overrideTimeout ?? repro.Manifest.TimeoutSeconds;

        var executor = new ReproExecutor(_console);

        var options = new ReproExecutionOptions
        {
            UseProjectReference = useProjectReference.Value,
            Instances = instancesToRun,
            TimeoutSeconds = timeoutSeconds
        };

        return await executor.ExecuteAsync(repro, options, CancellationToken.None).ConfigureAwait(false);
    }

    private int UnknownCommand(string command)
    {
        _console.Error.WriteLine($"Unknown command '{command}'.");
        PrintUsage();
        return 1;
    }

    private void PrintListHeader()
    {
        _console.Out.WriteLine($"{"ID",-35} {"STATE",-7} {"TIMEOUT",-9} {"FAILING",-12} {"TAGS",-24} TITLE");
        _console.Out.WriteLine(new string('-', 92));
    }

    private void PrintListRow(ReproManifest manifest)
    {
        var tags = manifest.Tags.Count > 0 ? string.Join(",", manifest.Tags) : "-";
        var failing = manifest.FailingSince ?? "-";
        var timeout = manifest.TimeoutSeconds + "s";

        _console.Out.WriteLine($"{manifest.Id,-35} {manifest.State,-7} {timeout,-9} {failing,-12} {tags,-24} {manifest.Title}");
    }

    private void PrintInvalid(DiscoveredRepro repro)
    {
        _console.Error.WriteLine($"INVALID  {repro.RelativeManifestPath.Replace(Path.DirectorySeparatorChar, '/')}");
        foreach (var error in repro.Validation.Errors)
        {
            _console.Error.WriteLine($"  - {error}");
        }
    }

    private void PrintManifest(DiscoveredRepro repro)
    {
        var manifest = repro.Manifest!;

        _console.Out.WriteLine($"Id: {manifest.Id}");
        _console.Out.WriteLine($"Title: {manifest.Title}");
        _console.Out.WriteLine($"State: {manifest.State}");
        _console.Out.WriteLine($"TimeoutSeconds: {manifest.TimeoutSeconds}");
        _console.Out.WriteLine($"RequiresParallel: {manifest.RequiresParallel}");
        _console.Out.WriteLine($"DefaultInstances: {manifest.DefaultInstances}");
        _console.Out.WriteLine($"SharedDatabaseKey: {manifest.SharedDatabaseKey ?? "-"}");
        _console.Out.WriteLine($"FailingSince: {manifest.FailingSince ?? "-"}");
        _console.Out.WriteLine($"Tags: {(manifest.Tags.Count > 0 ? string.Join(", ", manifest.Tags) : "-")}");
        _console.Out.WriteLine($"Args: {(manifest.Args.Count > 0 ? string.Join(" ", manifest.Args) : "-")}");

        if (manifest.Issues.Count > 0)
        {
            _console.Out.WriteLine("Issues:");
            foreach (var issue in manifest.Issues)
            {
                _console.Out.WriteLine($"  - {issue}");
            }
        }
    }

    private void PrintValidationResult(DiscoveredRepro repro)
    {
        if (repro.IsValid)
        {
            _console.Out.WriteLine($"VALID    {repro.RelativeManifestPath.Replace(Path.DirectorySeparatorChar, '/')}");
        }
        else
        {
            PrintInvalid(repro);
        }
    }
}
