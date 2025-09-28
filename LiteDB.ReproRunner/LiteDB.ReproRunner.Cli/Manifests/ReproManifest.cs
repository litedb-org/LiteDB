namespace LiteDB.ReproRunner.Cli.Manifests;

/// <summary>
/// Describes a reproducible scenario definition consumed by the CLI.
/// </summary>
internal sealed class ReproManifest
{
    /// <summary>
    /// Initializes a new instance of the <see cref="ReproManifest"/> class.
    /// </summary>
    /// <param name="id">The unique identifier for the repro.</param>
    /// <param name="title">A friendly title describing the repro.</param>
    /// <param name="issues">Issue tracker links associated with the repro.</param>
    /// <param name="failingSince">The date or version when the repro began failing.</param>
    /// <param name="timeoutSeconds">The timeout for the repro in seconds.</param>
    /// <param name="requiresParallel">Indicates whether the repro requires parallel instances.</param>
    /// <param name="defaultInstances">The default number of instances to launch.</param>
    /// <param name="sharedDatabaseKey">The key used to share database state between instances.</param>
    /// <param name="args">Additional command-line arguments passed to the repro host.</param>
    /// <param name="tags">Tags describing the repro characteristics.</param>
    /// <param name="state">The current state of the repro (e.g., red, green).</param>
    public ReproManifest(
        string id,
        string title,
        IReadOnlyList<string> issues,
        string? failingSince,
        int timeoutSeconds,
        bool requiresParallel,
        int defaultInstances,
        string? sharedDatabaseKey,
        IReadOnlyList<string> args,
        IReadOnlyList<string> tags,
        string state)
    {
        Id = id;
        Title = title;
        Issues = issues;
        FailingSince = failingSince;
        TimeoutSeconds = timeoutSeconds;
        RequiresParallel = requiresParallel;
        DefaultInstances = defaultInstances;
        SharedDatabaseKey = sharedDatabaseKey;
        Args = args;
        Tags = tags;
        State = state;
    }

    /// <summary>
    /// Gets the unique identifier for the repro.
    /// </summary>
    public string Id { get; }

    /// <summary>
    /// Gets the human-readable title for the repro.
    /// </summary>
    public string Title { get; }

    /// <summary>
    /// Gets the issue tracker links associated with the repro.
    /// </summary>
    public IReadOnlyList<string> Issues { get; }

    /// <summary>
    /// Gets the optional date or version indicating when the repro started failing.
    /// </summary>
    public string? FailingSince { get; }

    /// <summary>
    /// Gets the timeout for the repro, in seconds.
    /// </summary>
    public int TimeoutSeconds { get; }

    /// <summary>
    /// Gets a value indicating whether the repro requires parallel execution.
    /// </summary>
    public bool RequiresParallel { get; }

    /// <summary>
    /// Gets the default number of instances to execute.
    /// </summary>
    public int DefaultInstances { get; }

    /// <summary>
    /// Gets the shared database key used to coordinate state between instances.
    /// </summary>
    public string? SharedDatabaseKey { get; }

    /// <summary>
    /// Gets the additional command-line arguments passed to the repro host.
    /// </summary>
    public IReadOnlyList<string> Args { get; }

    /// <summary>
    /// Gets the descriptive tags applied to the repro.
    /// </summary>
    public IReadOnlyList<string> Tags { get; }

    /// <summary>
    /// Gets the declared state of the repro (for example, "red").
    /// </summary>
    public string State { get; }
}
