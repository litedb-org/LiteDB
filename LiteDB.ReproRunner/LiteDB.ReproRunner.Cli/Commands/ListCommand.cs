using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using LiteDB.ReproRunner.Cli.Infrastructure;
using LiteDB.ReproRunner.Cli.Manifests;
using Spectre.Console;
using Spectre.Console.Cli;

namespace LiteDB.ReproRunner.Cli.Commands;

internal sealed class ListCommand : Command<ListCommandSettings>
{
    private readonly IAnsiConsole _console;
    private readonly ReproRootLocator _rootLocator;

    /// <summary>
    /// Initializes a new instance of the <see cref="ListCommand"/> class.
    /// </summary>
    /// <param name="console">The console used to render output.</param>
    /// <param name="rootLocator">Resolves the repro root directory.</param>
    public ListCommand(IAnsiConsole console, ReproRootLocator rootLocator)
    {
        _console = console ?? throw new ArgumentNullException(nameof(console));
        _rootLocator = rootLocator ?? throw new ArgumentNullException(nameof(rootLocator));
    }

    /// <summary>
    /// Executes the list command.
    /// </summary>
    /// <param name="context">The Spectre command context.</param>
    /// <param name="settings">The user-provided settings.</param>
    /// <returns>The process exit code.</returns>
    public override int Execute(CommandContext context, ListCommandSettings settings)
    {
        var repository = new ManifestRepository(_rootLocator.ResolveRoot(settings.Root));
        var manifests = repository.Discover();
        var valid = manifests.Where(x => x.IsValid).ToList();
        var invalid = manifests.Where(x => !x.IsValid).ToList();
        Regex? filter = null;

        if (!string.IsNullOrWhiteSpace(settings.Filter))
        {
            try
            {
                filter = new Regex(settings.Filter, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
            }
            catch (ArgumentException ex)
            {
                _console.MarkupLine($"[red]Invalid --filter pattern[/]: {Markup.Escape(ex.Message)}");
                return 1;
            }
        }

        if (filter is not null)
        {
            valid = valid.Where(repro => MatchesFilter(repro, filter)).ToList();
            invalid = invalid.Where(repro => MatchesFilter(repro, filter)).ToList();
        }

        if (settings.Json)
        {
            WriteJson(_console, valid, invalid);
        }
        else
        {
            CliOutput.PrintList(_console, valid);

            foreach (var repro in invalid)
            {
                CliOutput.PrintInvalid(_console, repro);
            }
        }

        if (settings.Strict && invalid.Count > 0)
        {
            return 2;
        }

        return 0;
    }

    private static bool MatchesFilter(DiscoveredRepro repro, Regex filter)
    {
        var identifier = repro.Manifest?.Id ?? repro.RawId ?? repro.RelativeManifestPath;
        return identifier is not null && filter.IsMatch(identifier);
    }

    private static void WriteJson(IAnsiConsole console, IReadOnlyList<DiscoveredRepro> valid, IReadOnlyList<DiscoveredRepro> invalid)
    {
        var validEntries = valid
            .Where(item => item.Manifest is not null)
            .Select(item => item.Manifest!)
            .Select(manifest =>
            {
                var supports = manifest.Supports.Count > 0 ? manifest.Supports : new[] { "any" };
                object? os = null;

                if (manifest.OsConstraints is not null &&
                    (manifest.OsConstraints.IncludePlatforms.Count > 0 ||
                     manifest.OsConstraints.IncludeLabels.Count > 0 ||
                     manifest.OsConstraints.ExcludePlatforms.Count > 0 ||
                     manifest.OsConstraints.ExcludeLabels.Count > 0))
                {
                    os = new
                    {
                        includePlatforms = manifest.OsConstraints.IncludePlatforms,
                        includeLabels = manifest.OsConstraints.IncludeLabels,
                        excludePlatforms = manifest.OsConstraints.ExcludePlatforms,
                        excludeLabels = manifest.OsConstraints.ExcludeLabels
                    };
                }

                return new
                {
                    name = manifest.Id,
                    supports,
                    os
                };
            })
            .ToList();

        var invalidEntries = invalid
            .Select(item => new
            {
                name = item.Manifest?.Id ?? item.RawId ?? item.RelativeManifestPath.Replace(Path.DirectorySeparatorChar, '/'),
                errors = item.Validation.Errors.ToArray()
            })
            .Where(entry => entry.errors.Length > 0)
            .ToList();

        var payload = new
        {
            repros = validEntries,
            invalid = invalidEntries.Count > 0 ? invalidEntries : null
        };

        var options = new JsonSerializerOptions
        {
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };

        var json = JsonSerializer.Serialize(payload, options);
        console.WriteLine(json);
    }
}
