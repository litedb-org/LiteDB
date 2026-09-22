using LiteDB.Fuzz.Targets;

namespace LiteDB.Fuzz;

internal static class Program
{
    private static readonly IFuzzTarget[] Targets =
    {
        new QueryFuzzer(), new LinqCacheFuzzer(), new TransactionFuzzer(), new WalFuzzer(),
        new PageFuzzer(), new IndexFuzzer(), new SharedProcessFuzzer(), new BsonFuzzer(),
        new ParserFuzzer(), new MapperFuzzer(), new StorageFuzzer(), new RebuildFuzzer(),
        new VectorFuzzer(), new SortFuzzer(), new ValueFuzzer(), new IntegrityFuzzer(),
        new SnapshotFuzzer(), new ThreadedSnapshotFuzzer(), new ConcurrentFuzzer(),
        new PowerLossFuzzer(), new BoundaryFuzzer(), new ReadOnlyFuzzer(), new SqlDmlFuzzer(),
        new CompatibilityFuzzer(), new RecoveryFuzzer(), new ChaosFuzzer(), new ApiBoundaryFuzzer(),
        new StorageFailureFuzzer(), new OracleSelfTestFuzzer(), new PressureFuzzer(), new MalformedFileFuzzer(),
        new RebuildTransitionFuzzer(), new ConflictFuzzer(), new CompactCodecFuzzer(),
        new CompactStorageFuzzer()
    };

    internal static async Task<int> Main(string[] args)
    {
        FuzzOptions options;
        try { options = FuzzOptions.Parse(args); }
        catch (HelpRequestedException) { PrintHelp(); return 0; }
        catch (Exception error) { Console.Error.WriteLine(error.Message); PrintHelp(); return 2; }

        if (options.Child == "shared") return SharedProcessFuzzer.RunChild(options);
        if (options.Child == "snapshot-reader") return SnapshotFuzzer.RunChild(options);
        if (options.List)
        {
            foreach (var target in Targets) Console.WriteLine($"{target.Name,-14} {target.Description}");
            return 0;
        }
        var replaying = options.Replay != null;
        if (replaying)
        {
            try { options = FuzzOptions.FromReplay(FuzzArtifacts.ReadReplay(options.Replay), options.ArtifactDirectory, options.Replay); }
            catch (Exception error) { Console.Error.WriteLine($"Invalid replay file: {error.Message}"); return 2; }
        }

        IFuzzTarget[] selected;
        try { selected = SelectTargets(options.Targets); }
        catch (Exception error) { Console.Error.WriteLine(error.Message); return 2; }

        if (options.Child == "target")
        {
            if (selected.Length != 1) return 2;
            var result = await RunAsync(selected[0], options, options.WorkerId);
            Console.WriteLine($"{(result.Passed ? "PASS" : "FAIL")} {result.Target} seed={result.Seed} artifacts={result.Directory}");
            return result.Passed ? 0 : 1;
        }
        if (options.Child == "trial")
        {
            using var trial = new FuzzContext(selected[0].Name, options.Seed, options.Count, null,
                options.RunDirectory, options.DurationReplay, options.InputFile, options.HeartbeatFile);
            string identity = null;
            try { await selected[0].RunAsync(trial); }
            catch (Exception error) { identity = FailureIdentity.Get(error); }
            await File.WriteAllTextAsync(options.Ledger, identity ?? string.Empty);
            return identity == null ? 0 : 1;
        }

        var requestedRuns = selected.Length * options.Workers;
        TimeSpan? allocatedDuration = null;
        if (options.Duration.HasValue)
        {
            var parallel = Math.Min(FuzzProcessRunner.MaxParallelism, requestedRuns);
            allocatedDuration = TimeSpan.FromTicks(options.Duration.Value.Ticks * parallel / requestedRuns);
            Console.WriteLine($"TOTAL BUDGET {options.Duration.Value:c}: {requestedRuns} runs, " +
                $"{parallel} slots, {allocatedDuration.Value:c} per target/worker shard");
        }
        var runs = selected.SelectMany(target => Enumerable.Range(0, options.Workers)
            .Select(worker => FuzzProcessRunner.RunEpochsAsync(target, options, worker, allocatedDuration))).ToList();
        if (!replaying)
        {
            foreach (var corpusCase in FuzzCorpus.Load().Where(item => selected.Any(target => target.Name == item.Target)))
            {
                var target = selected.Single(item => item.Name == corpusCase.Target);
                runs.Add(FuzzProcessRunner.RunEpochsAsync(target,
                    FuzzOptions.FromCorpus(corpusCase, options.ArtifactDirectory), 0));
            }
            foreach (var corpusCase in FuzzCorpus.LoadInteresting(options.ArtifactDirectory)
                .Where(item => selected.Any(target => target.Name == item.Target)))
            {
                var target = selected.Single(item => item.Name == corpusCase.Target);
                runs.Add(FuzzProcessRunner.RunEpochsAsync(target,
                    FuzzOptions.FromCorpus(corpusCase, options.ArtifactDirectory), 0));
            }
            foreach (var corpusCase in FuzzCorpus.LoadCoverage(options.ArtifactDirectory)
                .Where(item => selected.Any(target => target.Name == item.Target)))
            {
                var target = selected.Single(item => item.Name == corpusCase.Target);
                runs.Add(FuzzProcessRunner.RunEpochsAsync(target,
                    FuzzOptions.FromCorpus(corpusCase, options.ArtifactDirectory), 0));
            }
        }
        var results = (await Task.WhenAll(runs)).SelectMany(result => result).ToArray();
        FuzzArtifacts.MergeInterestingCorpus(results, options.ArtifactDirectory);
        if (options.CoverageGuided) FuzzArtifacts.MergeCoverageCorpus(results, options.ArtifactDirectory);
        var failed = results.Count(result => !result.Passed);
        Console.WriteLine($"FUZZ SUMMARY: {results.Length - failed} passed, {failed} failed");
        foreach (var result in results)
            Console.WriteLine($"{(result.Passed ? "PASS" : "FAIL")} {result.Target} seed={result.Seed} artifacts={result.Directory}");
        return failed == 0 ? 0 : 1;
    }

    private static async Task<RunResult> RunAsync(IFuzzTarget target, FuzzOptions options, int worker)
    {
        var seed = options.Seed;
        var directory = options.RunDirectory ??
            FuzzArtifacts.CreateRunDirectory(options.ArtifactDirectory, target.Name, seed, worker);
        var started = DateTimeOffset.UtcNow;
        Exception failure = null;
        using var context = new FuzzContext(target.Name, seed, options.Count, options.Duration, directory,
            options.DurationReplay, options.InputFile, options.HeartbeatFile);
        Console.WriteLine($"START {target.Name} seed={seed} count={options.Count} worker={worker}");
        try
        {
            await Task.Run(() => target.RunAsync(context));
            if (options.ExpectedInputHash != null &&
                !string.Equals(options.ExpectedInputHash, context.Input.Hash(), StringComparison.OrdinalIgnoreCase))
            {
                throw new FuzzFailureException("CORPUS_INPUT_CONTRACT_DRIFT",
                    $"Corpus input changed: expected {options.ExpectedInputHash}, actual {context.Input.Hash()}.");
            }
            if (options.ExpectedTraceHash != null &&
                !string.Equals(options.ExpectedTraceHash, context.TraceHash(), StringComparison.OrdinalIgnoreCase))
            {
                throw new FuzzFailureException("CORPUS_TRACE_CONTRACT_DRIFT",
                    $"Corpus trace changed: expected {options.ExpectedTraceHash}, actual {context.TraceHash()}.");
            }
        }
        catch (Exception error)
        {
            failure = error;
            var failureText = error.ToString();
            Console.Error.WriteLine($"FUZZ FAILURE {target.Name} seed={seed} step={context.Steps}\n{failureText}");
            await File.WriteAllTextAsync(Path.Combine(directory, "failure-before-minimization.txt"), failureText);
            context.MinimizedCount = await MinimizeAsync(target, context, FailureIdentity.Get(error));
        }
        await FuzzArtifacts.WriteResultAsync(context, started, failure);
        return new RunResult(target.Name, seed, directory, failure == null);
    }

    private static async Task<int?> MinimizeAsync(IFuzzTarget target, FuzzContext failed, string failureId)
    {
        const int maximumSteps = 100_000;
        var low = 1;
        var high = Math.Min(Math.Max(1, failed.Steps), maximumSteps);
        if (await Replay(high) != failureId) return null;
        while (low < high)
        {
            var middle = low + (high - low) / 2;
            if (await Replay(middle) == failureId) high = middle;
            else low = middle + 1;
        }
        return high;

        async Task<string> Replay(int count)
        {
            var directory = Path.Combine(failed.DirectoryPath, "minimization", count.ToString());
            return await FuzzProcessRunner.RunTrialAsync(target.Name, failed.Seed, count,
                failed.DurationBound, directory, failed.Input.OutputPath);
        }
    }

    private static IFuzzTarget[] SelectTargets(IEnumerable<string> names)
    {
        var requested = names.ToArray();
        if (requested.Any(name => name.Equals("all", StringComparison.OrdinalIgnoreCase))) return Targets;
        return requested.Select(name => Targets.FirstOrDefault(target => target.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
            ?? throw new ArgumentException($"Unknown target '{name}'. Use --list to see target names.")).Distinct().ToArray();
    }

    private static void PrintHelp()
    {
        Console.WriteLine("LiteDB deterministic fuzz runner");
        Console.WriteLine("  --target <name[,name]|all>  target(s) to run (default: all)");
        Console.WriteLine("  --seed <int>                first deterministic seed");
        Console.WriteLine("  --count <int>               cases/operations per target");
        Console.WriteLine("  --duration <hh:mm:ss|Nm>    total wall-clock budget shared across selected runs");
        Console.WriteLine("  --epoch-duration <Nm>       fresh-process epoch bound (default: 30s)");
        Console.WriteLine("  --hang-timeout <Nm>         no-progress watchdog (default: 90s)");
        Console.WriteLine("  --workers <int>             parallel deterministic seed shards");
        Console.WriteLine("  --artifact-dir <path>       raw traces and summaries");
        Console.WriteLine("  --replay <replay.json>      replay one saved target/seed/count");
        Console.WriteLine("  --coverage-guided           retain seeds that add new LiteDB IL-range coverage");
        Console.WriteLine("  --determinism-check         rerun and compare input/trace hashes");
        Console.WriteLine("  --list                      list targets");
    }

}

internal sealed record RunResult(string Target, int Seed, string Directory, bool Passed);
