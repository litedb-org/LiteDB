namespace LiteDB.ReproRunner.Cli;

internal sealed class ReproRootLocator
{
    private readonly string? _defaultRoot;

    public ReproRootLocator(string? defaultRoot = null)
    {
        _defaultRoot = string.IsNullOrWhiteSpace(defaultRoot) ? null : defaultRoot;
    }

    public string ResolveRoot(string? rootOverride)
    {
        var candidateRoot = string.IsNullOrWhiteSpace(rootOverride) ? _defaultRoot : rootOverride;

        if (!string.IsNullOrEmpty(candidateRoot))
        {
            var candidate = Path.GetFullPath(candidateRoot!);
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
}
