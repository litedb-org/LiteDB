using System.Linq;
using System.Text.Json;

namespace LiteDB.ReproRunner.Cli;

internal sealed class ManifestRepository
{
    private readonly string _rootPath;
    private readonly string _reprosPath;
    private readonly ManifestValidator _validator = new();

    public ManifestRepository(string rootPath)
    {
        _rootPath = Path.GetFullPath(rootPath);
        _reprosPath = Path.Combine(_rootPath, "Repros");
    }

    public string RootPath => _rootPath;

    public IReadOnlyList<DiscoveredRepro> Discover()
    {
        if (!Directory.Exists(_reprosPath))
        {
            return Array.Empty<DiscoveredRepro>();
        }

        var repros = new List<DiscoveredRepro>();

        foreach (var manifestPath in Directory.EnumerateFiles(_reprosPath, "repro.json", SearchOption.AllDirectories))
        {
            repros.Add(LoadManifest(manifestPath));
        }

        ApplyDuplicateValidation(repros);

        repros.Sort((left, right) =>
        {
            var leftKey = left.Manifest?.Id ?? left.RawId ?? left.RelativeManifestPath;
            var rightKey = right.Manifest?.Id ?? right.RawId ?? right.RelativeManifestPath;
            return string.Compare(leftKey, rightKey, StringComparison.OrdinalIgnoreCase);
        });

        return repros;
    }

    private DiscoveredRepro LoadManifest(string manifestPath)
    {
        var validation = new ManifestValidationResult();
        ReproManifest? manifest = null;
        string? rawId = null;

        try
        {
            using var stream = File.OpenRead(manifestPath);
            using var document = JsonDocument.Parse(stream);
            manifest = _validator.Validate(document.RootElement, validation, out rawId);
        }
        catch (JsonException ex)
        {
            validation.AddError($"Invalid JSON: {ex.Message}");
        }
        catch (IOException ex)
        {
            validation.AddError($"Unable to read manifest: {ex.Message}");
        }
        catch (UnauthorizedAccessException ex)
        {
            validation.AddError($"Unable to read manifest: {ex.Message}");
        }

        var directory = Path.GetDirectoryName(manifestPath)!;
        var relativeManifestPath = Path.GetRelativePath(_rootPath, manifestPath);

        var projectFiles = Directory.GetFiles(directory, "*.csproj", SearchOption.TopDirectoryOnly);
        string? projectPath = null;

        if (projectFiles.Length == 0)
        {
            validation.AddError("No project file (*.csproj) found in repro directory.");
        }
        else if (projectFiles.Length > 1)
        {
            validation.AddError("Multiple project files found in repro directory. Only one project is supported.");
        }
        else
        {
            projectPath = projectFiles[0];
        }

        return new DiscoveredRepro(_rootPath, directory, manifestPath, relativeManifestPath, projectPath, manifest, validation, rawId);
    }

    private void ApplyDuplicateValidation(List<DiscoveredRepro> repros)
    {
        var groups = repros
            .Where(r => !string.IsNullOrWhiteSpace(r.Manifest?.Id ?? r.RawId))
            .GroupBy(r => (r.Manifest?.Id ?? r.RawId)!, StringComparer.OrdinalIgnoreCase);

        foreach (var group in groups)
        {
            if (group.Count() <= 1)
            {
                continue;
            }

            var allPaths = group
                .Select(r => r.RelativeManifestPath.Replace(Path.DirectorySeparatorChar, '/'))
                .ToList();

            foreach (var repro in group)
            {
                var currentPath = repro.RelativeManifestPath.Replace(Path.DirectorySeparatorChar, '/');
                var others = allPaths.Where(path => !string.Equals(path, currentPath, StringComparison.OrdinalIgnoreCase));
                var othersList = string.Join(", ", others);
                repro.Validation.AddError($"$.id: duplicate identifier also defined in {othersList}");
            }
        }
    }
}
