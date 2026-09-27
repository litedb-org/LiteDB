using System.Text.Json;
using Xunit;

namespace LiteDB.Fuzz.Tests;

public sealed class FuzzCorpusPublication_Tests
{
    [Fact]
    public async Task Longer_seed_only_run_does_not_displace_verified_input()
    {
        using var root = new TemporaryDirectory();
        var original = await Run(root.Path, "first", 1, 1);
        FuzzArtifacts.MergeInterestingCorpus(new[] { original }, root.Path);
        var before = Assert.Single(FuzzCorpus.LoadInteresting(root.Path));
        var longer = await Run(root.Path, "longer", 1, 2);
        File.Delete(Path.Combine(longer.Directory, "input.bin"));

        FuzzArtifacts.MergeInterestingCorpus(new[] { longer }, root.Path);

        Assert.Equal(before, Assert.Single(FuzzCorpus.LoadInteresting(root.Path)));
        var seedOnly = before with { Count = 100, InputFile = null, InputHash = null, TraceHash = null };
        File.AppendAllText(Path.Combine(root.Path, "interesting-corpus.jsonl"), System.Text.Json.JsonSerializer.Serialize(seedOnly) + "\n");
        Assert.Equal(before, Assert.Single(FuzzCorpus.LoadInteresting(root.Path)));
        Assert.Equal(before, Assert.Single(FuzzCorpus.SelectReplayed(new[] { before, seedOnly }, x => x)));
    }

    [Fact]
    public async Task Interrupted_publication_keeps_previous_manifest_and_inputs_and_retry_prunes_orphans()
    {
        using var root = new TemporaryDirectory();
        var first = await Run(root.Path, "first", 1, 1);
        FuzzArtifacts.MergeInterestingCorpus(new[] { first }, root.Path);
        var manifestPath = Path.Combine(root.Path, "interesting-corpus.jsonl");
        var manifest = File.ReadAllBytes(manifestPath);
        var before = Assert.Single(FuzzCorpus.LoadInteresting(root.Path));
        var inputPath = Path.Combine(root.Path, before.InputFile);
        var input = File.ReadAllBytes(inputPath);
        var longer = await Run(root.Path, "longer", 1, 2);
        var broken = await Run(root.Path, "broken", 2, 1);
        var tracePath = Path.Combine(broken.Directory, "trace.jsonl");
        var trace = File.ReadAllText(tracePath);
        File.WriteAllText(tracePath, "invalid json");

        Assert.ThrowsAny<JsonException>(() => FuzzArtifacts.MergeInterestingCorpus(new[] { longer, broken }, root.Path));

        Assert.Equal(manifest, File.ReadAllBytes(manifestPath));
        Assert.Equal(input, File.ReadAllBytes(inputPath));
        File.WriteAllText(tracePath, trace);
        FuzzArtifacts.MergeInterestingCorpus(new[] { longer, broken }, root.Path);
        var selected = FuzzCorpus.LoadInteresting(root.Path);
        Assert.Equal(2, selected.Count);
        Assert.Equal(2, selected.Single(x => x.Seed == 1).Count);
        Assert.False(File.Exists(inputPath));
        Assert.Equal(selected.Select(x => Path.Combine(root.Path, x.InputFile)).OrderBy(x => x),
            Directory.GetFiles(Path.Combine(root.Path, "interesting-inputs")).OrderBy(x => x));
        var published = File.ReadAllBytes(manifestPath);
        FuzzArtifacts.MergeInterestingCorpus(new[] { longer, broken }, root.Path);
        Assert.Equal(published, File.ReadAllBytes(manifestPath));
    }

    private static async Task<RunResult> Run(string root, string name, int seed, int count)
    {
        var path = Path.Combine(root, name);
        using var context = new FuzzContext("publication", seed, count, null, path);
        while (context.Next())
        {
            context.Random.Next();
            context.Trace("state", context.Steps);
            context.ObserveNovelty("same-signature", context.Steps);
        }
        await FuzzArtifacts.WriteResultAsync(context, DateTimeOffset.UtcNow, null);
        return new RunResult("publication", seed, path, true);
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        internal string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "corpus-publish-" + Guid.NewGuid());
        internal TemporaryDirectory() => Directory.CreateDirectory(Path);
        public void Dispose() => Directory.Delete(Path, true);
    }
}
