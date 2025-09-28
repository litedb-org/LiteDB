using System.Text;
using LiteDB.ReproRunner.Cli.Manifests;

namespace LiteDB.ReproRunner.Cli.Execution;

/// <summary>
/// Produces deterministic run directories for repro build and execution artifacts.
/// </summary>
internal sealed class RunDirectoryPlanner
{
    private readonly string _runsRoot;

    /// <summary>
    /// Initializes a new instance of the <see cref="RunDirectoryPlanner"/> class.
    /// </summary>
    public RunDirectoryPlanner()
    {
        _runsRoot = Path.Combine(AppContext.BaseDirectory, "runs");
    }

    /// <summary>
    /// Creates a plan that describes where a repro variant should build and execute.
    /// </summary>
    /// <param name="repro">The repro being executed.</param>
    /// <param name="manifestIdentifier">The identifier to use for the manifest directory.</param>
    /// <param name="variantIdentifier">The identifier to use for the variant directory.</param>
    /// <param name="displayName">The display label shown to the user.</param>
    /// <param name="useProjectReference">Indicates whether the repro should build against the source project.</param>
    /// <returns>The planned variant with prepared directories.</returns>
    public RunVariantPlan CreateVariantPlan(
        DiscoveredRepro repro,
        string manifestIdentifier,
        string variantIdentifier,
        string displayName,
        bool useProjectReference)
    {
        if (repro is null)
        {
            throw new ArgumentNullException(nameof(repro));
        }

        if (string.IsNullOrWhiteSpace(manifestIdentifier))
        {
            manifestIdentifier = repro.Manifest?.Id ?? repro.RawId ?? "repro";
        }

        if (string.IsNullOrWhiteSpace(variantIdentifier))
        {
            variantIdentifier = useProjectReference ? "ver_latest" : "ver_package";
        }

        var manifestSegment = Sanitize(manifestIdentifier);
        var variantSegment = Sanitize(variantIdentifier);

        var variantRoot = Path.Combine(_runsRoot, manifestSegment, variantSegment);
        PurgeDirectory(variantRoot);
        Directory.CreateDirectory(variantRoot);

        var buildOutput = Path.Combine(variantRoot, "build");
        Directory.CreateDirectory(buildOutput);

        var executionRoot = Path.Combine(variantRoot, "run");
        Directory.CreateDirectory(executionRoot);

        return new RunVariantPlan(
            repro,
            displayName,
            useProjectReference,
            manifestIdentifier,
            variantIdentifier,
            variantRoot,
            buildOutput,
            executionRoot,
            static path => PurgeDirectory(path));
    }

    private static string Sanitize(string value)
    {
        var buffer = new StringBuilder(value.Length);
        foreach (var ch in value)
        {
            if (Array.IndexOf(Path.GetInvalidFileNameChars(), ch) >= 0)
            {
                buffer.Append('_');
            }
            else
            {
                buffer.Append(ch);
            }
        }

        return buffer.ToString();
    }

    private static void PurgeDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch
        {
        }
    }
}

/// <summary>
/// Represents a planned repro variant along with its associated directories.
/// </summary>
internal sealed class RunVariantPlan : IDisposable
{
    private readonly Action<string> _cleanup;

    /// <summary>
    /// Initializes a new instance of the <see cref="RunVariantPlan"/> class.
    /// </summary>
    /// <param name="repro">The repro that produced this plan.</param>
    /// <param name="displayName">The friendly name presented to the user.</param>
    /// <param name="useProjectReference">Indicates whether a project reference build should be used.</param>
    /// <param name="manifestIdentifier">The identifier used for the manifest directory.</param>
    /// <param name="variantIdentifier">The identifier used for the variant directory.</param>
    /// <param name="rootDirectory">The root directory allocated for the variant.</param>
    /// <param name="buildOutputDirectory">The directory where build outputs are written.</param>
    /// <param name="executionRootDirectory">The directory where execution artifacts are stored.</param>
    /// <param name="cleanup">The cleanup delegate invoked when the plan is disposed.</param>
    public RunVariantPlan(
        DiscoveredRepro repro,
        string displayName,
        bool useProjectReference,
        string manifestIdentifier,
        string variantIdentifier,
        string rootDirectory,
        string buildOutputDirectory,
        string executionRootDirectory,
        Action<string> cleanup)
    {
        Repro = repro ?? throw new ArgumentNullException(nameof(repro));
        DisplayName = displayName;
        UseProjectReference = useProjectReference;
        ManifestIdentifier = manifestIdentifier;
        VariantIdentifier = variantIdentifier;
        RootDirectory = rootDirectory ?? throw new ArgumentNullException(nameof(rootDirectory));
        BuildOutputDirectory = buildOutputDirectory ?? throw new ArgumentNullException(nameof(buildOutputDirectory));
        ExecutionRootDirectory = executionRootDirectory ?? throw new ArgumentNullException(nameof(executionRootDirectory));
        _cleanup = cleanup ?? throw new ArgumentNullException(nameof(cleanup));
    }

    /// <summary>
    /// Gets the repro that produced this plan.
    /// </summary>
    public DiscoveredRepro Repro { get; }

    /// <summary>
    /// Gets the friendly name presented to the user.
    /// </summary>
    public string DisplayName { get; }

    /// <summary>
    /// Gets a value indicating whether the build uses project references.
    /// </summary>
    public bool UseProjectReference { get; }

    /// <summary>
    /// Gets the identifier used for the manifest directory.
    /// </summary>
    public string ManifestIdentifier { get; }

    /// <summary>
    /// Gets the identifier used for the variant directory.
    /// </summary>
    public string VariantIdentifier { get; }

    /// <summary>
    /// Gets the root directory allocated for the variant.
    /// </summary>
    public string RootDirectory { get; }

    /// <summary>
    /// Gets the directory where build outputs are written.
    /// </summary>
    public string BuildOutputDirectory { get; }

    /// <summary>
    /// Gets the directory where execution artifacts are stored.
    /// </summary>
    public string ExecutionRootDirectory { get; }

    /// <summary>
    /// Cleans up the planned directories when the plan is disposed.
    /// </summary>
    public void Dispose()
    {
        try
        {
            _cleanup(RootDirectory);
        }
        catch
        {
        }
    }
}
