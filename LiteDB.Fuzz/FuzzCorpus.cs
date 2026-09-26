using System.Text.Json;

namespace LiteDB.Fuzz;

internal sealed record FuzzCorpusCase(string Target, int Seed, int Count, string Reason,
    string InputHash = null, string TraceHash = null, bool DurationBound = false,
    double? OriginalDurationSeconds = null, string InputFile = null, string Signature = null);

internal sealed record FuzzCorpusFile(int SchemaVersion, FuzzCorpusCase[] Cases);

internal static class FuzzCorpus
{
    internal const int MaximumRetainedCasesPerTarget = 8;

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
            var item = ParseInteresting(line);
            if (item == null) continue;
            var key = (item.Target, item.Seed);
            if (!cases.TryGetValue(key, out var retained) || StrongerThan(item, retained)) cases[key] = item;
        }
        return Bound(cases.Values);
    }

    internal static FuzzCorpusCase ParseInteresting(string line)
    {
        using var document = JsonDocument.Parse(line);
        var value = document.RootElement;
        if (!value.TryGetProperty("Target", out var target) || !value.TryGetProperty("Seed", out var seed)) return null;
        if (value.TryGetProperty("Count", out _))
            return System.Text.Json.JsonSerializer.Deserialize<FuzzCorpusCase>(line);
        if (!value.TryGetProperty("step", out var step)) return null;
        value.TryGetProperty("signature", out var signature);
        return new FuzzCorpusCase(target.GetString(), seed.GetInt32(), Math.Max(1, step.GetInt32()),
            "Retained semantic-coverage signature from a previous campaign.",
            Signature: signature.ValueKind == JsonValueKind.String ? signature.GetString() : null);
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

    private static bool StrongerThan(FuzzCorpusCase candidate, FuzzCorpusCase retained)
    {
        if (candidate.Count != retained.Count) return candidate.Count > retained.Count;
        return Fidelity(candidate) > Fidelity(retained);
    }

    private static int Fidelity(FuzzCorpusCase item) =>
        (item.InputFile == null ? 0 : 4) + (item.InputHash == null ? 0 : 2) +
        (item.TraceHash == null ? 0 : 1) + (item.DurationBound ? 1 : 0);
}
