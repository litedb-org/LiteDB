using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;

namespace LiteDB.Fuzz;

internal sealed record FuzzReplay(string Target, int Seed, int Count, bool DurationBound = false,
    double? OriginalDurationSeconds = null, string FailureId = null, string InputFile = null,
    string InputHash = null, string TraceHash = null);

internal static class FuzzArtifacts
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
#if NET10_0_OR_GREATER
    private const string TargetFramework = "net10.0";
#else
    private const string TargetFramework = "net8.0";
#endif

    internal static string CreateRunDirectory(string root, string target, int seed, int worker)
    {
        var run = Guid.NewGuid().ToString("N")[..8];
        var name = $"{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}-{Safe(target)}-s{seed}-w{worker}-r{run}";
        return Path.GetFullPath(Path.Combine(root, name));
    }

    internal static async Task WriteResultAsync(FuzzContext context, DateTimeOffset started, Exception error)
    {
        var finished = DateTimeOffset.UtcNow;
        var sha = Git("rev-parse", "HEAD") ?? "unknown";
        var workingTreeDirty = !string.IsNullOrEmpty(Git("status", "--porcelain"));
        var result = new
        {
            schemaVersion = 1, status = error == null ? "passed" : "failed", target = context.Target,
            seed = context.Seed, count = context.Count, steps = context.Steps, startedUtc = started,
            finishedUtc = finished, durationSeconds = (finished - started).TotalSeconds, gitSha = sha, workingTreeDirty,
            os = RuntimeInformation.OSDescription, runtime = RuntimeInformation.FrameworkDescription,
            architecture = RuntimeInformation.ProcessArchitecture.ToString(),
            culture = CultureInfo.CurrentCulture.Name, timezone = TimeZoneInfo.Local.Id,
            globalizationInvariant = string.Equals(Environment.GetEnvironmentVariable("DOTNET_SYSTEM_GLOBALIZATION_INVARIANT"), "1", StringComparison.Ordinal),
            durationBound = context.DurationBound, requestedDurationSeconds = context.RequestedDuration?.TotalSeconds,
            inputHash = context.Input.Hash(), traceHash = context.TraceHash(),
            failureId = error == null ? null : FailureIdentity.Get(error), minimizedCount = context.MinimizedCount,
            metrics = context.Metrics, error = error?.ToString()
        };
        await File.WriteAllTextAsync(Path.Combine(context.DirectoryPath, "run.json"),
            System.Text.Json.JsonSerializer.Serialize(result, JsonOptions));
        await File.WriteAllTextAsync(Path.Combine(context.DirectoryPath, "replay.json"),
            System.Text.Json.JsonSerializer.Serialize(Replay(context, error), JsonOptions));
        if (context.MinimizedCount.HasValue)
            await File.WriteAllTextAsync(Path.Combine(context.DirectoryPath, "minimized-replay.json"),
                System.Text.Json.JsonSerializer.Serialize(new FuzzReplay(context.Target, context.Seed,
                    context.MinimizedCount.Value, context.DurationBound, context.RequestedDuration?.TotalSeconds,
                    error == null ? null : FailureIdentity.Get(error), "input.bin", context.Input.Hash(), context.TraceHash()), JsonOptions));
        CopyRegisteredFiles(context);

        var summary = new StringBuilder()
            .AppendLine($"# LiteDB fuzz result: {context.Target}").AppendLine()
            .AppendLine($"- Status: **{(error == null ? "PASS" : "FAIL")}**")
            .AppendLine($"- Seed: `{context.Seed}`")
            .AppendLine($"- Steps: `{context.Steps}` / requested `{context.Count}`")
            .AppendLine($"- Duration: `{(finished - started).TotalSeconds:F3}s`")
            .AppendLine($"- Git SHA: `{sha}`")
            .AppendLine($"- Working tree dirty: `{workingTreeDirty}`")
            .AppendLine($"- Environment: `{RuntimeInformation.OSDescription}`, `{RuntimeInformation.FrameworkDescription}`, `{RuntimeInformation.ProcessArchitecture}`")
            .AppendLine($"- Culture/timezone: `{CultureInfo.CurrentCulture.Name}` / `{TimeZoneInfo.Local.Id}`")
            .AppendLine($"- Failure ID: `{(error == null ? "n/a" : FailureIdentity.Get(error))}`")
            .AppendLine($"- Input SHA-256: `{context.Input.Hash()}`")
            .AppendLine($"- Trace SHA-256: `{context.TraceHash()}`")
            .AppendLine($"- Replay: `dotnet run --project LiteDB.Fuzz -c Release -f {TargetFramework} -- --replay {Path.Combine(context.DirectoryPath, "replay.json")}`");
        if (context.Metrics.Count != 0)
        {
            summary.AppendLine().AppendLine("## Metrics").AppendLine();
            foreach (var metric in context.Metrics) summary.AppendLine($"- {metric.Key}: `{metric.Value}`");
        }
        if (error != null) summary.AppendLine().AppendLine("## Failure").AppendLine().AppendLine("```").AppendLine(error.ToString()).AppendLine("```");
        if (context.MinimizedCount.HasValue)
            summary.AppendLine().AppendLine($"- Automatically minimized failing prefix: `{context.MinimizedCount}` steps (`minimized-replay.json`).");
        await File.WriteAllTextAsync(Path.Combine(context.DirectoryPath, "summary.md"), summary.ToString());
    }

    internal static FuzzReplay ReadReplay(string path) =>
        System.Text.Json.JsonSerializer.Deserialize<FuzzReplay>(File.ReadAllText(path), new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
        ?? throw new InvalidDataException("Replay file was empty.");

    internal static void PruneSuccessfulDurationRun(string directory)
    {
        var replayPath = Path.Combine(directory, "replay.json");
        var replay = ReadReplay(replayPath);
        if (!replay.DurationBound || replay.FailureId != null) return;

        var removedFiles = 0;
        long removedBytes = 0;
        foreach (var path in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
            .Where(path => Path.GetFileName(path) is "input.bin" or "input-offsets.jsonl" ||
                path.EndsWith(".db", StringComparison.OrdinalIgnoreCase)))
        {
            removedBytes += new FileInfo(path).Length;
            File.Delete(path);
            removedFiles++;
        }

        var seedReplay = replay with { InputFile = null };
        File.WriteAllText(replayPath, System.Text.Json.JsonSerializer.Serialize(seedReplay, JsonOptions));
        File.WriteAllText(Path.Combine(directory, "retention.json"),
            System.Text.Json.JsonSerializer.Serialize(new
            {
                policy = "successful-duration-run",
                removedFiles,
                removedBytes,
                retained = new[] { "run metadata and hashes", "seed replay", "trace", "novelty data", "coverage" },
                failureArtifactsAlwaysRetained = true
            }, JsonOptions));
    }

    internal static async Task WriteAbnormalTerminationAsync(string directory, string target, int seed,
        int count, DateTimeOffset started, int exitCode, bool hung, string output, string error, string input)
    {
        Directory.CreateDirectory(directory);
        var failureId = hung ? $"HANG_{Safe(target).ToUpperInvariant()}" :
            $"CHILD_EXIT_{Safe(target).ToUpperInvariant()}_{exitCode}";
        var replayInput = "input.bin";
        var localInput = Path.Combine(directory, replayInput);
        if (input != null && File.Exists(input) && !File.Exists(localInput)) File.Copy(input, localInput, true);
        var inputHash = File.Exists(localInput) ? Hash(localInput) : null;
        var tracePath = Path.Combine(directory, "trace.jsonl");
        var traceHash = File.Exists(tracePath) ? Hash(tracePath) : null;
        var result = new
        {
            schemaVersion = 1, status = hung ? "hung" : "crashed", target, seed, count,
            startedUtc = started, finishedUtc = DateTimeOffset.UtcNow, exitCode, failureId,
            standardOutput = output, standardError = error, inputHash, traceHash
        };
        await File.WriteAllTextAsync(Path.Combine(directory, "run.json"),
            System.Text.Json.JsonSerializer.Serialize(result, JsonOptions));
        await File.WriteAllTextAsync(Path.Combine(directory, "replay.json"),
            System.Text.Json.JsonSerializer.Serialize(new FuzzReplay(target, seed, count, false, null,
                failureId, File.Exists(localInput) ? replayInput : null, inputHash, traceHash), JsonOptions));
        await File.WriteAllTextAsync(Path.Combine(directory, "stdout.txt"), output);
        await File.WriteAllTextAsync(Path.Combine(directory, "stderr.txt"), error);
        await File.WriteAllTextAsync(Path.Combine(directory, "summary.md"),
            $"# LiteDB fuzz result: {target}\n\n- Status: **{(hung ? "HANG" : "CRASH")}**\n" +
            $"- Seed: `{seed}`\n- Exit code: `{exitCode}`\n- Failure ID: `{failureId}`\n");
    }

    internal static void MergeInterestingCorpus(IEnumerable<RunResult> results, string root)
    {
        var entries = new SortedDictionary<string, string>(StringComparer.Ordinal);
        var retained = Path.Combine(root, "interesting-corpus.jsonl");
        if (File.Exists(retained))
        {
            foreach (var line in File.ReadLines(retained)) Add(line);
        }
        foreach (var result in results.OrderBy(item => item.Target).ThenBy(item => item.Seed))
        {
            var path = Path.Combine(result.Directory, "interesting.jsonl");
            if (!File.Exists(path)) continue;
            foreach (var line in File.ReadLines(path))
            {
                Add(line);
            }
        }
        Directory.CreateDirectory(root);
        File.WriteAllLines(retained, entries.Values);

        void Add(string line)
        {
            using var document = JsonDocument.Parse(line);
            if (!document.RootElement.TryGetProperty("signature", out var signature)) return;
            entries.TryAdd(signature.GetString(), line);
        }
    }

    internal static void MergeCoverageCorpus(IEnumerable<RunResult> results, string root)
    {
        Directory.CreateDirectory(root);
        var signaturesPath = Path.Combine(root, "coverage-signatures.txt");
        var corpusPath = Path.Combine(root, "coverage-corpus.jsonl");
        var signatures = File.Exists(signaturesPath)
            ? new HashSet<string>(File.ReadLines(signaturesPath), StringComparer.Ordinal)
            : new HashSet<string>(StringComparer.Ordinal);
        var cases = File.Exists(corpusPath) ? File.ReadAllLines(corpusPath).ToList() : new List<string>();

        foreach (var result in results.Where(item => item.Passed)
            .OrderBy(item => item.Target).ThenBy(item => item.Seed))
        {
            var path = Path.Combine(result.Directory, "coverage.xml");
            if (!File.Exists(path)) continue;
            var covered = ReadCoveredEngineRanges(path);
            var novel = covered.Count(signatures.Add);
            File.WriteAllText(Path.Combine(result.Directory, "coverage-novelty.json"),
                System.Text.Json.JsonSerializer.Serialize(
                    new { engineRanges = covered.Count, novelEngineRanges = novel }, JsonOptions));
            if (novel == 0) continue;
            var replay = ReadReplay(Path.Combine(result.Directory, "replay.json"));
            cases.Add(System.Text.Json.JsonSerializer.Serialize(new FuzzCorpusCase(replay.Target, replay.Seed, replay.Count,
                $"Added {novel} new LiteDB execution-coverage ranges.")));
        }

        File.WriteAllLines(signaturesPath, signatures.OrderBy(value => value, StringComparer.Ordinal));
        var boundedCases = cases.Distinct(StringComparer.Ordinal)
            .Select(line => new { Line = line, Case = System.Text.Json.JsonSerializer.Deserialize<FuzzCorpusCase>(line) })
            .Where(item => item.Case != null)
            .GroupBy(item => item.Case.Target, StringComparer.Ordinal)
            .SelectMany(group => group.TakeLast(8))
            .Select(item => item.Line);
        File.WriteAllLines(corpusPath, boundedCases);
    }

    private static HashSet<string> ReadCoveredEngineRanges(string path)
    {
        var document = XDocument.Load(path);
        var covered = new HashSet<string>(StringComparer.Ordinal);
        foreach (var module in document.Descendants("module")
            .Where(element => string.Equals((string)element.Attribute("name"), "LiteDB.dll", StringComparison.OrdinalIgnoreCase)))
        {
            foreach (var function in module.Descendants("function"))
            {
                var functionKey = $"{(string)function.Attribute("token")}:{(string)function.Attribute("type_name")}:{(string)function.Attribute("name")}";
                foreach (var range in function.Descendants("range")
                    .Where(element => !string.Equals((string)element.Attribute("covered"), "no", StringComparison.OrdinalIgnoreCase)))
                {
                    covered.Add($"{functionKey}:{(string)range.Attribute("source_id")}:{(string)range.Attribute("start_line")}:" +
                        $"{(string)range.Attribute("start_column")}:{(string)range.Attribute("end_line")}:{(string)range.Attribute("end_column")}");
                }
            }
        }
        return covered;
    }

    private static FuzzReplay Replay(FuzzContext context, Exception error)
    {
        var count = context.DurationBound ? Math.Max(1, context.Steps) : context.Count;
        return new FuzzReplay(context.Target, context.Seed, count, context.DurationBound,
            context.RequestedDuration?.TotalSeconds, error == null ? null : FailureIdentity.Get(error),
            "input.bin", context.Input.Hash(), context.TraceHash());
    }

    private static void CopyRegisteredFiles(FuzzContext context)
    {
        foreach (var source in context.Files.Where(File.Exists))
        {
            var target = Path.Combine(context.DirectoryPath, "state-" + Path.GetFileName(source));
            if (!string.Equals(Path.GetFullPath(source), Path.GetFullPath(target), StringComparison.Ordinal)) File.Copy(source, target, true);
        }
    }

    private static string Git(params string[] arguments)
    {
        try
        {
            var start = new ProcessStartInfo("git") { RedirectStandardOutput = true, UseShellExecute = false };
            foreach (var argument in arguments) start.ArgumentList.Add(argument);
            using var process = Process.Start(start);
            var output = process.StandardOutput.ReadToEnd().Trim();
            process.WaitForExit();
            return process.ExitCode == 0 ? output : null;
        }
        catch { return null; }
    }

    private static string Safe(string text) => new(text.Select(ch => char.IsLetterOrDigit(ch) || ch == '-' ? ch : '_').ToArray());

    private static string Hash(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(stream));
    }
}
