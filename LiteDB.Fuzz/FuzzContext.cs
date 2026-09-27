using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;

namespace LiteDB.Fuzz;

internal sealed class FuzzContext : IDisposable
{
    private readonly StreamWriter _trace;
    private readonly DateTimeOffset _deadline;
    private int _traceLines;
    private int _steps;
    private readonly HashSet<string> _novelty = new(StringComparer.Ordinal);
    private readonly StreamWriter _interesting;
    private readonly StreamWriter _inputOffsets;

    private readonly string _heartbeatPath;
    private DateTimeOffset _lastHeartbeat;
    private readonly List<string> _stepFiles = new();

    internal FuzzContext(string target, int seed, int count, TimeSpan? duration, string directory,
        bool durationBoundReplay = false, string inputPath = null, string heartbeatPath = null)
    {
        Target = target;
        Seed = seed;
        Count = count;
        DirectoryPath = directory;
        Directory.CreateDirectory(directory);
        Input = new FuzzInputRandom(seed, Path.Combine(directory, "input.bin"), inputPath);
        Random = Input;
        _trace = CreateJsonLinesWriter(Path.Combine(directory, "trace.jsonl"));
        _interesting = CreateJsonLinesWriter(Path.Combine(directory, "interesting.jsonl"));
        _inputOffsets = CreateJsonLinesWriter(Path.Combine(directory, "input-offsets.jsonl"));
        _deadline = duration.HasValue ? DateTimeOffset.UtcNow + duration.Value : DateTimeOffset.MaxValue;
        DurationBound = duration.HasValue || durationBoundReplay;
        RequestedDuration = duration;
        _heartbeatPath = heartbeatPath;
        TouchHeartbeat(true);
    }

    internal string Target { get; }
    internal int Seed { get; }
    internal int Count { get; }
    internal Random Random { get; }
    internal FuzzInputRandom Input { get; }
    internal string DirectoryPath { get; }
    internal Dictionary<string, object> Metrics { get; } = new();
    internal List<string> Files { get; } = new();
    internal int Steps => _steps;
    internal bool DurationBound { get; }
    internal TimeSpan? RequestedDuration { get; }
    internal int? MinimizedCount { get; set; }

    internal string TraceHash()
    {
        _trace.Flush();
        using var stream = new FileStream(Path.Combine(DirectoryPath, "trace.jsonl"),
            FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    internal bool Next()
    {
        if (_steps >= Count && _deadline == DateTimeOffset.MaxValue) return false;
        if (DateTimeOffset.UtcNow >= _deadline) return false;
        // The previous step passed, so its scratch databases are no longer evidence.
        DeleteStepFiles();
        _steps++;
        _inputOffsets.WriteLine(System.Text.Json.JsonSerializer.Serialize(
            new { step = _steps, byteOffset = Input.Position }));
        TouchHeartbeat(false);
        return true;
    }

    internal void Trace(string operation, object detail = null)
    {
        const int fullTraceLimit = 50_000;
        if (_traceLines == fullTraceLimit)
        {
            _trace.WriteLine(System.Text.Json.JsonSerializer.Serialize(new
            {
                step = _steps,
                operation = "trace-sampling",
                detail = new { fullTraceLimit, interval = 1000 }
            }));
            _traceLines++;
        }
        if (_traceLines > fullTraceLimit && _steps % 1000 != 0) return;
        _trace.WriteLine(System.Text.Json.JsonSerializer.Serialize(new { step = _steps, operation, detail }));
        _traceLines++;
    }

    internal void ObserveNovelty(string category, params object[] features)
    {
        var value = System.Text.Json.JsonSerializer.Serialize(new { category, features });
        var signature = System.Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))[..16];
        if (!_novelty.Add(signature)) return;
        _interesting.WriteLine(System.Text.Json.JsonSerializer.Serialize(
            new { Target, Seed, step = _steps, category, signature, features }));
        Metrics["novelStates"] = _novelty.Count;
    }

    internal void PulseHeartbeat() => TouchHeartbeat(true);

    internal void Check(bool condition, string message,
        [CallerFilePath] string file = "", [CallerLineNumber] int line = 0)
    {
        if (!condition) throw new FuzzFailureException(FailureIdentity.FromCallSite(Target, file, line), message);
    }

    internal string RegisterFile(string path)
    {
        if (!Files.Contains(path, StringComparer.Ordinal)) Files.Add(path);
        return path;
    }

    /// <summary>
    /// A database used by the current step only. It is retained if this step fails, and deleted
    /// (with its -log/-tmp/-backup companions) when the next step starts. Deterministic replay
    /// recreates it, so long campaigns keep one step's files instead of every step's.
    /// </summary>
    internal string StepFile(string name)
    {
        var path = RegisterFile(Path.Combine(DirectoryPath, name));
        _stepFiles.Add(path);
        return path;
    }

    private void DeleteStepFiles()
    {
        for (var i = _stepFiles.Count - 1; i >= 0; i--)
        {
            var path = _stepFiles[i];
            var stem = Path.GetFileNameWithoutExtension(path);
            var companions = Directory.Exists(DirectoryPath)
                ? Directory.EnumerateFiles(DirectoryPath, stem + "-*").Where(IsCompanion)
                : Enumerable.Empty<string>();
            var deleted = true;
            foreach (var file in companions.Append(path).ToArray())
            {
                try { if (File.Exists(file)) File.Delete(file); }
                catch (IOException) { deleted = false; }
                catch (UnauthorizedAccessException) { deleted = false; }
            }
            // A handle that is still open is retried at the next step.
            if (!deleted) continue;
            _stepFiles.RemoveAt(i);
            Files.Remove(path);

            bool IsCompanion(string file)
            {
                var suffix = Path.GetFileName(file).Substring(stem.Length + 1);
                return suffix.StartsWith("log", StringComparison.Ordinal) ||
                    suffix.StartsWith("tmp", StringComparison.Ordinal) ||
                    suffix.StartsWith("backup", StringComparison.Ordinal);
            }
        }
    }

    public void Dispose()
    {
        _interesting.Dispose();
        _inputOffsets.Dispose();
        _trace.Dispose();
        Input.Dispose();
    }

    private static StreamWriter CreateJsonLinesWriter(string path) => new(path, false)
    {
        AutoFlush = true,
        NewLine = "\n"
    };

    private void TouchHeartbeat(bool force)
    {
        if (_heartbeatPath == null) return;
        var now = DateTimeOffset.UtcNow;
        if (!force && now - _lastHeartbeat < TimeSpan.FromSeconds(1)) return;
        File.WriteAllText(_heartbeatPath, $"{now:O} step={_steps}");
        _lastHeartbeat = now;
    }
}

internal sealed class FuzzFailureException : Exception
{
    internal FuzzFailureException(string message,
        [CallerFilePath] string file = "", [CallerLineNumber] int line = 0)
        : this(FailureIdentity.FromCallSite("FUZZ", file, line), message) { }

    internal FuzzFailureException(string failureId, string message) : base(message)
    {
        FailureId = failureId;
    }

    internal string FailureId { get; }
}

internal static class FailureIdentity
{
    internal static string Get(Exception error) => error is FuzzFailureException fuzz
        ? fuzz.FailureId : error.GetType().FullName;

    internal static string FromCallSite(string target, string file, int line)
    {
        var name = Path.GetFileNameWithoutExtension(file);
        return $"{Safe(target)}_{Safe(name)}_L{line}".ToUpperInvariant();
    }

    private static string Safe(string value) => new(value.Select(character =>
        char.IsLetterOrDigit(character) ? character : '_').ToArray());
}
