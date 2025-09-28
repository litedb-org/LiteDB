namespace LiteDB.ReproRunner.Cli;

internal sealed class DiscoveredRepro
{
    public DiscoveredRepro(
        string rootPath,
        string directoryPath,
        string manifestPath,
        string relativeManifestPath,
        string? projectPath,
        ReproManifest? manifest,
        ManifestValidationResult validation,
        string? rawId)
    {
        RootPath = rootPath;
        DirectoryPath = directoryPath;
        ManifestPath = manifestPath;
        RelativeManifestPath = relativeManifestPath;
        ProjectPath = projectPath;
        Manifest = manifest;
        Validation = validation;
        RawId = rawId;
    }

    public string RootPath { get; }

    public string DirectoryPath { get; }

    public string ManifestPath { get; }

    public string RelativeManifestPath { get; }

    public string? ProjectPath { get; }

    public ReproManifest? Manifest { get; }

    public ManifestValidationResult Validation { get; }

    public string? RawId { get; }

    public bool IsValid => Manifest is not null && Validation.IsValid;
}
