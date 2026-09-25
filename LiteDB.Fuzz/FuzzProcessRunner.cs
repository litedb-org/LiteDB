using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Text.Json;

namespace LiteDB.Fuzz;

internal static class FuzzProcessRunner
{
    private static readonly object ConsoleLock = new();
    internal static readonly int MaxParallelism = Math.Max(1, Math.Min(Environment.ProcessorCount, 4));
    private static readonly SemaphoreSlim ProcessSlots = new(MaxParallelism);
    // Workers merge into the shared corpus files under the artifact root one at a time.
    private static readonly object CorpusLock = new();

    internal static async Task<IReadOnlyList<RunResult>> RunEpochsAsync(
        IFuzzTarget target, FuzzOptions options, int worker, TimeSpan? allocatedDuration = null)
    {
        await ProcessSlots.WaitAsync();
        try
        {
            if (!allocatedDuration.HasValue)
            {
                // A count-bound run is one epoch: the same budget gate, before it starts.
                if (BudgetReached(options, target, worker))
                    return new[] { BudgetStop(target, options) };
                var result = await RunCoreAsync(target, options, worker, 0, null, options.InputFile);
                if (options.DeterminismCheck && result.Passed)
                    result = await VerifyDeterminismAsync(target, options, worker, result);
                return new[] { result };
            }

            var results = new List<RunResult>();
            var deadline = DateTimeOffset.UtcNow + allocatedDuration.Value;
            for (var epoch = 0; DateTimeOffset.UtcNow < deadline; epoch++)
            {
                var remaining = deadline - DateTimeOffset.UtcNow;
                var duration = remaining < options.EpochDuration ? remaining : options.EpochDuration;
                if (duration < TimeSpan.FromMilliseconds(100)) break;
                if (BudgetReached(options, target, worker))
                {
                    // A shard that never ran must not count as a passed campaign.
                    if (epoch == 0) results.Add(BudgetStop(target, options));
                    break;
                }
                var result = await RunCoreAsync(target, options, worker, epoch, duration, null);
                if (!result.Passed) result = FuzzFindingRegistry.Classify(result);
                else result = CompactPassedEpoch(result, options);
                results.Add(result);
                if (!ShouldContinueDiscovery(result)) break;
            }
            return results;
        }
        finally { ProcessSlots.Release(); }
    }

    /// <summary>
    /// Fold a passed epoch into the retained corpora and drop its bulky files now rather than at
    /// campaign end, so disk use stays proportional to one epoch instead of the whole campaign.
    /// </summary>
    private static RunResult CompactPassedEpoch(RunResult result, FuzzOptions options)
    {
        lock (CorpusLock)
        {
            FuzzArtifacts.MergeInterestingCorpus(new[] { result }, options.ArtifactDirectory);
            if (options.CoverageGuided) FuzzArtifacts.MergeCoverageCorpus(new[] { result }, options.ArtifactDirectory);
            if (result.PruneSuccessfulArtifacts) FuzzArtifacts.PruneSuccessfulDurationRun(result.Directory);
        }
        return result with { Compacted = true };
    }

    private static RunResult BudgetStop(IFuzzTarget target, FuzzOptions options) =>
        new(target.Name, options.Seed, options.ArtifactDirectory, Passed: false, BudgetStopped: true);

    private static bool BudgetReached(FuzzOptions options, IFuzzTarget target, int worker)
    {
        if (options.MaxArtifactBytes <= 0) return false;
        var used = FuzzArtifacts.DirectorySize(options.ArtifactDirectory);
        if (used < options.MaxArtifactBytes) return false;
        lock (ConsoleLock)
        {
            Console.WriteLine($"ARTIFACT BUDGET {options.MaxArtifactBytes / (1024 * 1024)} MB reached " +
                $"({used / (1024 * 1024)} MB in {options.ArtifactDirectory}); no further epochs for {target.Name} worker {worker}.");
        }
        return true;
    }

    private static async Task<RunResult> VerifyDeterminismAsync(
        IFuzzTarget target, FuzzOptions options, int worker, RunResult original)
    {
        var input = Path.Combine(original.Directory, "input.bin");
        var repeated = await RunCoreAsync(target, options, worker, 0, null, input, "determinism");
        if (!repeated.Passed) return repeated;
        var firstHash = TraceHash(original.Directory);
        var secondHash = TraceHash(repeated.Directory);
        if (firstHash == secondHash) return original;

        var path = Path.Combine(original.Directory, "determinism-failure.json");
        await File.WriteAllTextAsync(path, System.Text.Json.JsonSerializer.Serialize(new
        {
            failureId = $"NONDETERMINISTIC_{target.Name.ToUpperInvariant().Replace('-', '_')}",
            firstHash,
            secondHash,
            repeated.Directory
        }, new JsonSerializerOptions { WriteIndented = true }));
        return original with { Passed = false };
    }

    private static async Task<RunResult> RunCoreAsync(IFuzzTarget target, FuzzOptions options, int worker,
        int epoch, TimeSpan? duration, string input, string suffix = null)
    {
        var seed = unchecked(options.Seed + worker * 1_000_003 + epoch * 104_729);
        var directory = FuzzArtifacts.CreateRunDirectory(options.ArtifactDirectory,
            suffix == null ? target.Name : $"{target.Name}-{suffix}", seed, worker);
        Directory.CreateDirectory(directory);
        var heartbeat = Path.Combine(directory, "heartbeat.txt");
        var start = CreateStartInfo(options.CoverageGuided, directory);
        Add("--child", "target");
        Add("--target", target.Name);
        Add("--seed", seed.ToString(CultureInfo.InvariantCulture));
        Add("--count", options.Count.ToString(CultureInfo.InvariantCulture));
        Add("--worker-id", worker.ToString(CultureInfo.InvariantCulture));
        Add("--artifact-dir", options.ArtifactDirectory);
        Add("--run-directory", directory);
        Add("--heartbeat", heartbeat);
        if (input != null) Add("--input", input);
        if (options.ExpectedInputHash != null) Add("--expected-input-hash", options.ExpectedInputHash);
        if (options.ExpectedTraceHash != null) Add("--expected-trace-hash", options.ExpectedTraceHash);
        if (duration.HasValue) Add("--duration", duration.Value.ToString("c", CultureInfo.InvariantCulture));
        Add("--hang-timeout", options.HangTimeout.ToString("c", CultureInfo.InvariantCulture));
        Add("--minimization-timeout", options.MinimizationTimeout.ToString("c", CultureInfo.InvariantCulture));
        if (options.DurationReplay) start.ArgumentList.Add("--duration-mode");

        var started = DateTimeOffset.UtcNow;
        using var process = Process.Start(start) ??
            throw new InvalidOperationException($"Could not start isolated target {target.Name}.");
        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();
        var exitTask = process.WaitForExitAsync();
        var lastProgress = started;
        var hung = false;
        long? overBudget = null;
        while (!exitTask.IsCompleted)
        {
            await Task.WhenAny(exitTask, Task.Delay(TimeSpan.FromSeconds(2)));
            if (options.MaxArtifactBytes > 0)
            {
                var size = FuzzArtifacts.DirectorySize(directory);
                if (size > options.MaxArtifactBytes)
                {
                    overBudget = size;
                    try { process.Kill(entireProcessTree: true); }
                    catch (InvalidOperationException) { }
                    break;
                }
            }
            if (File.Exists(heartbeat)) lastProgress = File.GetLastWriteTimeUtc(heartbeat);
            if (DateTimeOffset.UtcNow - lastProgress <= options.HangTimeout) continue;
            hung = true;
            await TryCaptureDumpAsync(process, Path.Combine(directory, "hang.dmp"));
            try { process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
            break;
        }
        await exitTask;
        var output = await outputTask;
        var error = await errorTask;
        lock (ConsoleLock)
        {
            if (output.Length != 0) Console.Out.Write(output);
            if (error.Length != 0) Console.Error.Write(error);
        }

        if (overBudget.HasValue)
        {
            // A single run outgrew the whole budget: keep its metadata and output, drop the bulk,
            // and fail loudly so the offending target gets fixed instead of silently filling disks.
            await FuzzArtifacts.WriteArtifactBudgetFailureAsync(directory, target.Name, seed, options.Count,
                started, overBudget.Value, options.MaxArtifactBytes, output, error, input);
            return new RunResult(target.Name, seed, directory, false, duration.HasValue);
        }
        var passed = process.ExitCode == 0 && !hung;
        if (!File.Exists(Path.Combine(directory, "run.json")))
        {
            await FuzzArtifacts.WriteAbnormalTerminationAsync(directory, target.Name, seed, options.Count,
                started, process.ExitCode, hung, output, error, input);
        }
        return new RunResult(target.Name, seed, directory, passed, duration.HasValue);

        void Add(string name, string value)
        {
            start.ArgumentList.Add(name);
            start.ArgumentList.Add(value);
        }
    }

    internal static async Task<FuzzTrialResult> RunTrialAsync(string target, int seed, int count,
        bool durationMode, string directory, string input, TimeSpan timeout, string parentHeartbeat)
    {
        Directory.CreateDirectory(directory);
        var result = Path.Combine(directory, "failure-id.txt");
        var timeoutArtifact = Path.Combine(directory, "trial-timeout.json");
        File.Delete(result);
        File.Delete(timeoutArtifact);
        var heartbeat = Path.Combine(directory, "heartbeat.txt");
        var start = CreateStartInfo(false, directory);
        Add("--child", "trial");
        Add("--target", target);
        Add("--seed", seed.ToString(CultureInfo.InvariantCulture));
        Add("--count", count.ToString(CultureInfo.InvariantCulture));
        Add("--run-directory", directory);
        Add("--ledger", result);
        Add("--heartbeat", heartbeat);
        if (input != null) Add("--input", input);
        if (durationMode) start.ArgumentList.Add("--duration-mode");
        using var process = Process.Start(start) ??
            throw new InvalidOperationException($"Could not start minimization trial for {target}.");
        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();
        var exitTask = process.WaitForExitAsync();
        if (!await WaitForTrialExitAsync(exitTask, timeout, () => Touch(parentHeartbeat)))
        {
            try { process.Kill(entireProcessTree: true); }
            catch { }
            await Task.WhenAny(exitTask, Task.Delay(TimeSpan.FromSeconds(5)));
            await File.WriteAllTextAsync(timeoutArtifact,
                System.Text.Json.JsonSerializer.Serialize(new { target, seed, count, timeoutSeconds = timeout.TotalSeconds },
                    new JsonSerializerOptions { WriteIndented = true }));
            return new FuzzTrialResult(null, true);
        }
        await outputTask;
        await errorTask;
        if (!File.Exists(result)) return new FuzzTrialResult(null, false);
        var identity = File.ReadAllText(result);
        return new FuzzTrialResult(identity.Length == 0 ? null : identity, false);

        void Add(string name, string value)
        {
            start.ArgumentList.Add(name);
            start.ArgumentList.Add(value);
        }

        static void Touch(string path)
        {
            if (path == null) return;
            try { File.WriteAllText(path, $"{DateTimeOffset.UtcNow:O} minimizing"); }
            catch (IOException) { }
        }
    }

    internal static async Task<bool> WaitForTrialExitAsync(Task exitTask, TimeSpan timeout, Action pulse)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        pulse();
        while (!exitTask.IsCompleted)
        {
            var remaining = deadline - DateTimeOffset.UtcNow;
            if (remaining <= TimeSpan.Zero) return false;
            var delay = remaining < TimeSpan.FromMilliseconds(250) ? remaining : TimeSpan.FromMilliseconds(250);
            if (await Task.WhenAny(exitTask, Task.Delay(delay)) == exitTask) return true;
            pulse();
        }
        return true;
    }

    internal static bool ShouldContinueDiscovery(RunResult result) =>
        result.Passed || result.Finding?.AllowsDiscoveryToContinue == true;

    private static ProcessStartInfo CreateStartInfo(bool coverage, string directory)
    {
        var start = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        if (coverage)
        {
            start.ArgumentList.Add("tool");
            start.ArgumentList.Add("run");
            start.ArgumentList.Add("dotnet-coverage");
            start.ArgumentList.Add("collect");
            start.ArgumentList.Add("--output");
            start.ArgumentList.Add(Path.Combine(directory, "coverage.xml"));
            start.ArgumentList.Add("--output-format");
            start.ArgumentList.Add("xml");
            start.ArgumentList.Add("--nologo");
            start.ArgumentList.Add("dotnet");
        }
        start.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
        return start;
    }

    private static async Task TryCaptureDumpAsync(Process process, string path)
    {
        try
        {
            var start = new ProcessStartInfo("dotnet") { UseShellExecute = false };
            start.ArgumentList.Add("tool");
            start.ArgumentList.Add("run");
            start.ArgumentList.Add("dotnet-dump");
            start.ArgumentList.Add("collect");
            start.ArgumentList.Add("--process-id");
            start.ArgumentList.Add(process.Id.ToString(CultureInfo.InvariantCulture));
            start.ArgumentList.Add("--output");
            start.ArgumentList.Add(path);
            using var dump = Process.Start(start);
            if (dump == null) return;
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            try { await dump.WaitForExitAsync(timeout.Token); }
            catch (OperationCanceledException) { try { dump.Kill(true); } catch { } }
        }
        catch { }
    }

    private static string TraceHash(string directory)
    {
        var replay = FuzzArtifacts.ReadReplay(Path.Combine(directory, "replay.json"));
        return replay.TraceHash;
    }
}

internal sealed record FuzzTrialResult(string FailureId, bool TimedOut);
