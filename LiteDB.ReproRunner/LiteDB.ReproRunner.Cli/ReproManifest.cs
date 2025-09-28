namespace LiteDB.ReproRunner.Cli;

internal sealed class ReproManifest
{
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

    public string Id { get; }

    public string Title { get; }

    public IReadOnlyList<string> Issues { get; }

    public string? FailingSince { get; }

    public int TimeoutSeconds { get; }

    public bool RequiresParallel { get; }

    public int DefaultInstances { get; }

    public string? SharedDatabaseKey { get; }

    public IReadOnlyList<string> Args { get; }

    public IReadOnlyList<string> Tags { get; }

    public string State { get; }
}
