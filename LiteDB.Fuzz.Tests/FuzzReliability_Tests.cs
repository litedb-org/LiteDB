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

    [Fact]
    public void Finding_fingerprints_remove_volatile_values_without_merging_defects()
    {
        var first = FuzzFindingRegistry.Normalize(
            "orphan seed=17 step 44 page 0006:21 doc_id=81 count=3 " +
            "request_id=110ec58a-a0f2-4ac4-8393-c866d813b8d1 value_0x1234");
        var repeated = FuzzFindingRegistry.Normalize(
            "orphan seed=99 step 101 page 0004:08 doc_id=92 count=7 " +
            "request_id=220ec58a-a0f2-4ac4-8393-c866d813b8d2 value_0xabcd");
        var different = FuzzFindingRegistry.Normalize(
            "backlink seed=17 step 44 page 0006:21 doc_id=81 count=3");

        Assert.Equal(first, repeated);
        Assert.NotEqual(first, different);
    }

    [Fact]
    public void Discovery_continues_only_for_active_known_or_expected_findings()
    {
        using var directory = new TemporaryDirectory();
        var registry = Path.Combine(directory.Path, "known-findings.json");
        File.WriteAllText(registry, """
            {
              "schemaVersion": 1,
              "findings": [
                { "target": "vector", "fingerprint": "ORPHAN_PAGE_0006:21", "issue": 1, "status": "known" },
                { "target": "rebuild", "fingerprint": "CHECKSUM_COUNT=7", "issue": 2, "status": "expected" },
                { "target": "api", "fingerprint": "HANG_API", "issue": 3, "status": "fixed" }
              ]
            }
            """);
        var known = FuzzFindingRegistry.Resolve("vector", "ORPHAN_PAGE_0004:08", registry);
        var expected = FuzzFindingRegistry.Resolve("rebuild", "CHECKSUM_COUNT=99", registry);
        var fixedFinding = FuzzFindingRegistry.Resolve("api", "HANG_API", registry);
        var unknown = FuzzFindingRegistry.Resolve("value", "NEW_DEFECT", registry);

        Assert.True(FuzzProcessRunner.ShouldContinueDiscovery(
            new RunResult("vector", 1, directory.Path, false, true, known)));
        Assert.True(FuzzProcessRunner.ShouldContinueDiscovery(
            new RunResult("rebuild", 1, directory.Path, false, true, expected)));
        Assert.False(FuzzProcessRunner.ShouldContinueDiscovery(
            new RunResult("api", 1, directory.Path, false, true, fixedFinding)));
        Assert.False(FuzzProcessRunner.ShouldContinueDiscovery(
            new RunResult("value", 1, directory.Path, false, true, unknown)));
        Assert.False(FuzzFindingRegistry.ShouldMinimize(true, known));
        Assert.False(FuzzFindingRegistry.ShouldMinimize(true, expected));
        Assert.True(FuzzFindingRegistry.ShouldMinimize(true, fixedFinding));
        Assert.True(FuzzFindingRegistry.ShouldMinimize(true, unknown));
        Assert.True(FuzzFindingRegistry.ShouldMinimize(false, known));
        Assert.True(new RunResult("vector", 1, directory.Path, false, false, known).BlocksBuild);
        Assert.False(new RunResult("vector", 1, directory.Path, false, true, known).BlocksBuild);
    }

    [Fact]
    public async Task Step_files_keep_only_the_current_step_and_failures_copy_its_state()
    {
        using var directory = new TemporaryDirectory();
        var run = Path.Combine(directory.Path, "run");
        using (var context = new FuzzContext("step-files", 5, 3, null, run))
        {
            while (context.Next())
            {
                var file = context.StepFile($"scratch-{context.Steps}.db");
                File.WriteAllBytes(file, new byte[1024]);
                File.WriteAllBytes(Path.Combine(run, $"scratch-{context.Steps}-log.db"), new byte[512]);
            }
            await FuzzArtifacts.WriteResultAsync(context, DateTimeOffset.UtcNow,
                new FuzzFailureException("STEP_FILES_PROBE", "forced failure at the last step"));
        }

        Assert.False(File.Exists(Path.Combine(run, "scratch-1.db")));
        Assert.False(File.Exists(Path.Combine(run, "scratch-1-log.db")));
        Assert.False(File.Exists(Path.Combine(run, "scratch-2.db")));
        Assert.True(File.Exists(Path.Combine(run, "scratch-3.db")), "the failing step keeps its database");
        Assert.True(File.Exists(Path.Combine(run, "scratch-3-log.db")));
        Assert.True(File.Exists(Path.Combine(run, "state-scratch-3.db")), "a failure copies its registered state");
        Assert.True(File.Exists(Path.Combine(run, "input.bin")));
    }

    [Fact]
    public async Task Passed_runs_do_not_copy_registered_state()
    {
        using var directory = new TemporaryDirectory();
        var run = Path.Combine(directory.Path, "run");
        using (var context = new FuzzContext("state-copy", 6, 1, null, run))
        {
            Assert.True(context.Next());
            File.WriteAllBytes(context.RegisterFile(Path.Combine(run, "model.db")), new byte[2048]);
            await FuzzArtifacts.WriteResultAsync(context, DateTimeOffset.UtcNow, null);
        }

        Assert.True(File.Exists(Path.Combine(run, "model.db")));
        Assert.False(File.Exists(Path.Combine(run, "state-model.db")));
    }

    [Fact]
    public async Task Interesting_corpus_retains_only_the_inputs_the_next_campaign_replays()
    {
        using var root = new TemporaryDirectory();
        var runs = new List<RunResult>();
        for (var seed = 1; seed <= 12; seed++) runs.Add(await CreateInterestingRun(root.Path, seed));
        var inputs = Path.Combine(root.Path, "interesting-inputs");
        Directory.CreateDirectory(inputs);
        var orphan = Path.Combine(inputs, "orphan.bin");
        File.WriteAllBytes(orphan, new byte[16]);

        foreach (var run in runs) FuzzArtifacts.MergeInterestingCorpus(new[] { run }, root.Path);

        var replayed = FuzzCorpus.LoadInteresting(root.Path);
        var lines = File.ReadAllLines(Path.Combine(root.Path, "interesting-corpus.jsonl"));
        Assert.Equal(8, replayed.Count);
        Assert.Equal(8, lines.Length);
        Assert.False(File.Exists(orphan));
        Assert.Equal(replayed.Select(item => Path.GetFullPath(Path.Combine(root.Path, item.InputFile)))
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase),
            Directory.GetFiles(inputs).Select(Path.GetFullPath).OrderBy(path => path, StringComparer.OrdinalIgnoreCase));
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
