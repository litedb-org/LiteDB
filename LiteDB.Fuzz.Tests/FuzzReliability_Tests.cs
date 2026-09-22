using LiteDB.Fuzz.Targets;
using Xunit;

namespace LiteDB.Fuzz.Tests;

public sealed class FuzzReliability_Tests
{
    [Fact]
    public void Interesting_corpus_keeps_the_longest_exact_descriptor_for_a_seed()
    {
        using var directory = new TemporaryDirectory();
        var shorter = new FuzzCorpusCase("query", 17, 3, "short", Signature: "A");
        var longer = new FuzzCorpusCase("query", 17, 9, "long", "INPUT", "TRACE", true,
            30d, "interesting-inputs/B.bin", "B");
        File.WriteAllLines(Path.Combine(directory.Path, "interesting-corpus.jsonl"), new[]
        {
            System.Text.Json.JsonSerializer.Serialize(shorter),
            System.Text.Json.JsonSerializer.Serialize(longer)
        });

        var retained = Assert.Single(FuzzCorpus.LoadInteresting(directory.Path));

        Assert.Equal(9, retained.Count);
        Assert.True(retained.DurationBound);
        Assert.Equal("interesting-inputs/B.bin", retained.InputFile);
        Assert.Equal("INPUT", retained.InputHash);
        Assert.Equal("TRACE", retained.TraceHash);
    }

    [Fact]
    public async Task Retained_input_replays_the_state_that_caused_retention()
    {
        using var root = new TemporaryDirectory();
        var run = Path.Combine(root.Path, "run");
        var original = new List<int>();
        using (var context = new FuzzContext("corpus-probe", 71, 4, null, run, true))
        {
            while (context.Next())
            {
                original.Add(context.Random.Next());
                context.Trace("state", original.ToArray());
                context.ObserveNovelty("state", context.Steps);
            }
            await FuzzArtifacts.WriteResultAsync(context, DateTimeOffset.UtcNow, null);
        }
        FuzzArtifacts.MergeInterestingCorpus(new[]
        {
            new RunResult("corpus-probe", 71, run, true)
        }, root.Path);

        var retained = Assert.Single(FuzzCorpus.LoadInteresting(root.Path));
        Assert.Equal(4, retained.Count);
        Assert.NotNull(retained.InputFile);
        Assert.NotNull(retained.InputHash);
        Assert.NotNull(retained.TraceHash);

        var replayed = new List<int>();
        var replayDirectory = Path.Combine(root.Path, "replay");
        var options = FuzzOptions.FromCorpus(retained, root.Path);
        using (var context = new FuzzContext("corpus-probe", options.Seed, options.Count, null,
            replayDirectory, options.DurationReplay, options.InputFile))
        {
            while (context.Next())
            {
                replayed.Add(context.Random.Next());
                context.Trace("state", replayed.ToArray());
                context.ObserveNovelty("state", context.Steps);
            }
            Assert.Equal(retained.InputHash, context.Input.Hash());
            Assert.Equal(retained.TraceHash, context.TraceHash());
        }
        Assert.Equal(original, replayed);
    }

    [Fact]
    public async Task Equal_signatures_from_different_seeds_keep_distinct_inputs()
    {
        using var root = new TemporaryDirectory();
        var first = await CreateInterestingRun(root.Path, 101);
        var second = await CreateInterestingRun(root.Path, 202);

        FuzzArtifacts.MergeInterestingCorpus(new[] { first, second }, root.Path);

        var retained = FuzzCorpus.LoadInteresting(root.Path);
        Assert.Equal(2, retained.Count);
        Assert.Equal(2, retained.Select(item => item.InputFile).Distinct().Count());
        Assert.All(retained, item => Assert.True(File.Exists(Path.Combine(root.Path, item.InputFile))));
    }

    [Fact]
    public async Task Oracle_mutations_use_and_fail_the_real_verification_paths()
    {
        using var directory = new TemporaryDirectory();
        using var context = new FuzzContext("oracle-selftest", 1, 1, null, directory.Path);

        await new OracleSelfTestFuzzer().RunAsync(context);

        Assert.Equal(8, context.Metrics["controlledMutationsKilled"]);
    }

    [Fact]
    public void Power_loss_scenarios_are_seeded_replayable_and_multi_operation()
    {
        using var directory = new TemporaryDirectory();
        var first = Generate(10, Path.Combine(directory.Path, "first.bin"));
        var repeated = Generate(10, Path.Combine(directory.Path, "repeated.bin"));
        var different = Generate(11, Path.Combine(directory.Path, "different.bin"));

        Assert.Equal(System.Text.Json.JsonSerializer.Serialize(first),
            System.Text.Json.JsonSerializer.Serialize(repeated));
        Assert.NotEqual(System.Text.Json.JsonSerializer.Serialize(first),
            System.Text.Json.JsonSerializer.Serialize(different));
        Assert.All(first.Transactions, transaction => Assert.True(transaction.Operations.Length >= 2));
    }

    [Fact]
    public async Task Minimization_trial_wait_is_bounded_and_keeps_pulsing()
    {
        var pending = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pulses = 0;

        var exited = await FuzzProcessRunner.WaitForTrialExitAsync(pending.Task,
            TimeSpan.FromMilliseconds(30), () => pulses++);

        Assert.False(exited);
        Assert.True(pulses > 0);
    }

    [Fact]
    public async Task Timed_out_minimization_keeps_the_original_failure_classification()
    {
        using var directory = new TemporaryDirectory();
        var replayPath = Path.Combine(directory.Path, "source-replay.json");
        File.WriteAllText(replayPath, System.Text.Json.JsonSerializer.Serialize(
            new FuzzReplay("value", 2984, 1, InputHash: "deliberately-wrong")));
        var artifacts = Path.Combine(directory.Path, "artifacts");

        var exitCode = await Program.Main(new[]
        {
            "--replay", replayPath,
            "--artifact-dir", artifacts,
            "--hang-timeout", "5s",
            "--minimization-timeout", "0.000001s"
        });

        Assert.Equal(1, exitCode);
        var runDirectory = Assert.Single(Directory.GetDirectories(artifacts));
        var replay = FuzzArtifacts.ReadReplay(Path.Combine(runDirectory, "replay.json"));
        Assert.Equal("CORPUS_INPUT_CONTRACT_DRIFT", replay.FailureId);
        Assert.True(File.Exists(Path.Combine(runDirectory, "failure-before-minimization.txt")));
        Assert.Single(Directory.GetFiles(Path.Combine(runDirectory, "minimization"),
            "trial-timeout.json", SearchOption.AllDirectories));
    }

    private static PowerLossScenario Generate(int seed, string path)
    {
        using var random = new FuzzInputRandom(seed, path, null);
        return PowerLossScenario.Generate(random, 5);
    }

    private static async Task<RunResult> CreateInterestingRun(string root, int seed)
    {
        var run = Path.Combine(root, "run-" + seed);
        using var context = new FuzzContext("collision-probe", seed, 1, null, run);
        Assert.True(context.Next());
        _ = context.Random.Next();
        context.ObserveNovelty("same-signature", 1);
        await FuzzArtifacts.WriteResultAsync(context, DateTimeOffset.UtcNow, null);
        return new RunResult("collision-probe", seed, run, true);
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        internal TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "litedb-fuzz-tests-" + Guid.NewGuid());
            Directory.CreateDirectory(Path);
        }

        internal string Path { get; }

        public void Dispose() => Directory.Delete(Path, true);
    }
}
