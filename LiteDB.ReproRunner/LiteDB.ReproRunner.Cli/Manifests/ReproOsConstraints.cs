using System.Collections.Generic;

namespace LiteDB.ReproRunner.Cli.Manifests;

/// <summary>
/// Represents advanced operating system constraints declared by a repro manifest.
/// </summary>
internal sealed class ReproOsConstraints
{
    /// <summary>
    /// Initializes a new instance of the <see cref="ReproOsConstraints"/> class.
    /// </summary>
    /// <param name="includePlatforms">Platform families that must be included.</param>
    /// <param name="includeLabels">Specific runner labels that must be included.</param>
    /// <param name="excludePlatforms">Platform families that must be excluded.</param>
    /// <param name="excludeLabels">Specific runner labels that must be excluded.</param>
    public ReproOsConstraints(
        IReadOnlyList<string> includePlatforms,
        IReadOnlyList<string> includeLabels,
        IReadOnlyList<string> excludePlatforms,
        IReadOnlyList<string> excludeLabels)
    {
        IncludePlatforms = includePlatforms ?? Array.Empty<string>();
        IncludeLabels = includeLabels ?? Array.Empty<string>();
        ExcludePlatforms = excludePlatforms ?? Array.Empty<string>();
        ExcludeLabels = excludeLabels ?? Array.Empty<string>();
    }

    /// <summary>
    /// Gets the platform families that must be included.
    /// </summary>
    public IReadOnlyList<string> IncludePlatforms { get; }

    /// <summary>
    /// Gets the runner labels that must be included.
    /// </summary>
    public IReadOnlyList<string> IncludeLabels { get; }

    /// <summary>
    /// Gets the platform families that must be excluded.
    /// </summary>
    public IReadOnlyList<string> ExcludePlatforms { get; }

    /// <summary>
    /// Gets the runner labels that must be excluded.
    /// </summary>
    public IReadOnlyList<string> ExcludeLabels { get; }
}
