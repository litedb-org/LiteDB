namespace LiteDB.ReproRunner.Cli.Manifests;

/// <summary>
/// Represents a repro manifest discovered on disk along with its metadata.
/// </summary>
internal sealed class DiscoveredRepro
{
    /// <summary>
    /// Initializes a new instance of the <see cref="DiscoveredRepro"/> class.
    /// </summary>
    /// <param name="rootPath">The resolved root directory that contains all repros.</param>
    /// <param name="directoryPath">The directory where the repro resides.</param>
    /// <param name="manifestPath">The full path to the manifest file.</param>
    /// <param name="relativeManifestPath">The manifest path relative to the repro root.</param>
    /// <param name="projectPath">The optional project file path declared by the manifest.</param>
    /// <param name="manifest">The parsed manifest payload, if validation succeeded.</param>
    /// <param name="validation">The validation results produced while parsing.</param>
    /// <param name="rawId">The manifest identifier captured prior to validation.</param>
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

    /// <summary>
    /// Gets the resolved root directory that contains all repros.
    /// </summary>
    public string RootPath { get; }

    /// <summary>
    /// Gets the full path to the directory that contains the repro manifest.
    /// </summary>
    public string DirectoryPath { get; }

    /// <summary>
    /// Gets the full path to the manifest file on disk.
    /// </summary>
    public string ManifestPath { get; }

    /// <summary>
    /// Gets the manifest path relative to the repro root.
    /// </summary>
    public string RelativeManifestPath { get; }

    /// <summary>
    /// Gets the optional project file path declared in the manifest.
    /// </summary>
    public string? ProjectPath { get; }

    /// <summary>
    /// Gets the parsed manifest payload, if validation succeeded.
    /// </summary>
    public ReproManifest? Manifest { get; }

    /// <summary>
    /// Gets the validation results collected while processing the manifest.
    /// </summary>
    public ManifestValidationResult Validation { get; }

    /// <summary>
    /// Gets the manifest identifier captured prior to validation.
    /// </summary>
    public string? RawId { get; }

    /// <summary>
    /// Gets a value indicating whether the manifest was parsed successfully.
    /// </summary>
    public bool IsValid => Manifest is not null && Validation.IsValid;
}
