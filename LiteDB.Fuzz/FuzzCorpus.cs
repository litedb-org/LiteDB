using System.Text.Json;

namespace LiteDB.Fuzz;

internal sealed record FuzzCorpusCase(string Target, int Seed, int Count, string Reason,
    string InputHash = null, string TraceHash = null);

internal sealed record FuzzCorpusFile(int SchemaVersion, FuzzCorpusCase[] Cases);

internal static class FuzzCorpus
{
    private const int MaximumRetainedCasesPerTarget = 8;

    internal static IReadOnlyList<FuzzCorpusCase> Load()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Corpus", "regressions.json");
        var file = System.Text.Json.JsonSerializer.Deserialize<FuzzCorpusFile>(File.ReadAllText(path),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        if (file?.SchemaVersion != 1 || file.Cases == null)
            throw new InvalidDataException("Unsupported or empty fuzz regression corpus.");
        foreach (var item in file.Cases)
        {
            if (string.IsNullOrWhiteSpace(item.Target) || item.Count <= 0)
                throw new InvalidDataException("Every fuzz corpus case needs a target and positive count.");
        }
        return file.Cases;
    }

    internal static IReadOnlyList<FuzzCorpusCase> LoadInteresting(string root)
    {
        var path = Path.Combine(root, "interesting-corpus.jsonl");
        if (!File.Exists(path)) return Array.Empty<FuzzCorpusCase>();
        var cases = new Dictionary<(string Target, int Seed), FuzzCorpusCase>();
        foreach (var line in File.ReadLines(path))
        {
            using var document = JsonDocument.Parse(line);
            var value = document.RootElement;
            if (!value.TryGetProperty("Target", out var target) || !value.TryGetProperty("Seed", out var seed) ||
                !value.TryGetProperty("step", out var step)) continue;
            var item = new FuzzCorpusCase(target.GetString(), seed.GetInt32(), Math.Max(1, step.GetInt32()),
                "Retained semantic-coverage signature from a previous campaign.");
            cases.TryAdd((item.Target, item.Seed), item);
        }
        return Bound(cases.Values);
    }

    internal static IReadOnlyList<FuzzCorpusCase> LoadCoverage(string root)
    {
        var path = Path.Combine(root, "coverage-corpus.jsonl");
        if (!File.Exists(path)) return Array.Empty<FuzzCorpusCase>();
        var cases = new Dictionary<(string Target, int Seed), FuzzCorpusCase>();
        foreach (var line in File.ReadLines(path))
        {
            var item = System.Text.Json.JsonSerializer.Deserialize<FuzzCorpusCase>(line);
            if (item == null || string.IsNullOrWhiteSpace(item.Target) || item.Count <= 0) continue;
            cases.TryAdd((item.Target, item.Seed), item);
        }
        return Bound(cases.Values);
    }

    private static IReadOnlyList<FuzzCorpusCase> Bound(IEnumerable<FuzzCorpusCase> cases) => cases
        .GroupBy(item => item.Target, StringComparer.Ordinal)
        .SelectMany(group => group.TakeLast(MaximumRetainedCasesPerTarget))
        .ToArray();
}
