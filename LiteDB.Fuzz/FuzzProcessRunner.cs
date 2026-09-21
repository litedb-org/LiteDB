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

    internal static async Task<IReadOnlyList<RunResult>> RunEpochsAsync(
        IFuzzTarget target, FuzzOptions options, int worker, TimeSpan? allocatedDuration = null)
    {
        await ProcessSlots.WaitAsync();
        try
        {
            if (!allocatedDuration.HasValue)
            {
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
                var result = await RunCoreAsync(target, options, worker, epoch, duration, null);
                results.Add(result);
                if (!result.Passed) break;
            }
            return results;
        }
        finally { ProcessSlots.Release(); }
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
        if (options.DurationReplay) start.ArgumentList.Add("--duration-mode");

        var started = DateTimeOffset.UtcNow;
        using var process = Process.Start(start) ??
            throw new InvalidOperationException($"Could not start isolated target {target.Name}.");
        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();
        var exitTask = process.WaitForExitAsync();
        var lastProgress = started;
        var hung = false;
        while (!exitTask.IsCompleted)
        {
            await Task.WhenAny(exitTask, Task.Delay(TimeSpan.FromSeconds(2)));
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

        var passed = process.ExitCode == 0 && !hung;
        if (!File.Exists(Path.Combine(directory, "run.json")))
        {
            await FuzzArtifacts.WriteAbnormalTerminationAsync(directory, target.Name, seed, options.Count,
                started, process.ExitCode, hung, output, error, input);
        }
        if (passed && duration.HasValue) FuzzArtifacts.PruneSuccessfulDurationRun(directory);
        return new RunResult(target.Name, seed, directory, passed);

        void Add(string name, string value)
        {
            start.ArgumentList.Add(name);
            start.ArgumentList.Add(value);
        }
    }

    internal static async Task<string> RunTrialAsync(string target, int seed, int count,
        bool durationMode, string directory, string input)
    {
        Directory.CreateDirectory(directory);
        var result = Path.Combine(directory, "failure-id.txt");
        var start = CreateStartInfo(false, directory);
        Add("--child", "trial");
        Add("--target", target);
        Add("--seed", seed.ToString(CultureInfo.InvariantCulture));
        Add("--count", count.ToString(CultureInfo.InvariantCulture));
        Add("--run-directory", directory);
        Add("--ledger", result);
        if (input != null) Add("--input", input);
        if (durationMode) start.ArgumentList.Add("--duration-mode");
        using var process = Process.Start(start) ??
            throw new InvalidOperationException($"Could not start minimization trial for {target}.");
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        await output;
        await error;
        if (!File.Exists(result)) return null;
        var identity = File.ReadAllText(result);
        return identity.Length == 0 ? null : identity;

        void Add(string name, string value)
        {
            start.ArgumentList.Add(name);
            start.ArgumentList.Add(value);
        }
    }

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
