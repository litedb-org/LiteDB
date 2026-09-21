using System.Globalization;

namespace LiteDB.Fuzz;

internal sealed class FuzzOptions
{
    internal string[] Targets { get; private set; } = new[] { "all" };
    internal int Seed { get; private set; } = 2947;
    internal int Count { get; private set; } = 100;
    internal TimeSpan? Duration { get; private set; }
    internal int Workers { get; private set; } = 1;
    internal string ArtifactDirectory { get; private set; } = Path.Combine("artifacts_temp", "fuzz");
    internal string Replay { get; private set; }
    internal bool List { get; private set; }
    internal string Child { get; private set; }
    internal string Database { get; private set; }
    internal string Ledger { get; private set; }
    internal int WorkerId { get; private set; }
    internal int CrashAt { get; private set; } = -1;
    internal string RunDirectory { get; private set; }
    internal bool DurationReplay { get; private set; }
    internal bool CoverageGuided { get; private set; }
    internal string InputFile { get; private set; }
    internal string HeartbeatFile { get; private set; }
    internal TimeSpan HangTimeout { get; private set; } = TimeSpan.FromSeconds(90);
    internal TimeSpan EpochDuration { get; private set; } = TimeSpan.FromSeconds(30);
    internal bool DeterminismCheck { get; private set; }
    internal string ExpectedInputHash { get; private set; }
    internal string ExpectedTraceHash { get; private set; }

    internal static FuzzOptions Parse(string[] args)
    {
        var options = new FuzzOptions();
        for (var i = 0; i < args.Length; i++)
        {
            string Value() => ++i < args.Length ? args[i] : throw new ArgumentException($"Missing value for {args[i - 1]}");
            switch (args[i])
            {
                case "--target": options.Targets = Value().Split(',', StringSplitOptions.RemoveEmptyEntries); break;
                case "--seed": options.Seed = int.Parse(Value(), CultureInfo.InvariantCulture); break;
                case "--count": options.Count = Positive(Value(), "count"); break;
                case "--duration": options.Duration = ParseDuration(Value()); break;
                case "--workers": options.Workers = Positive(Value(), "workers"); break;
                case "--artifact-dir": options.ArtifactDirectory = Value(); break;
                case "--replay": options.Replay = Value(); break;
                case "--list": options.List = true; break;
                case "--child": options.Child = Value(); break;
                case "--database": options.Database = Value(); break;
                case "--ledger": options.Ledger = Value(); break;
                case "--worker-id": options.WorkerId = int.Parse(Value(), CultureInfo.InvariantCulture); break;
                case "--crash-at": options.CrashAt = int.Parse(Value(), CultureInfo.InvariantCulture); break;
                case "--run-directory": options.RunDirectory = Value(); break;
                case "--duration-mode": options.DurationReplay = true; break;
                case "--coverage-guided": options.CoverageGuided = true; break;
                case "--input": options.InputFile = Value(); break;
                case "--heartbeat": options.HeartbeatFile = Value(); break;
                case "--hang-timeout": options.HangTimeout = ParseDuration(Value()); break;
                case "--epoch-duration": options.EpochDuration = ParseDuration(Value()); break;
                case "--determinism-check": options.DeterminismCheck = true; break;
                case "--expected-input-hash": options.ExpectedInputHash = Value(); break;
                case "--expected-trace-hash": options.ExpectedTraceHash = Value(); break;
                case "--help": throw new HelpRequestedException();
                default: throw new ArgumentException($"Unknown option: {args[i]}");
            }
        }
        return options;
    }

    internal static FuzzOptions FromReplay(FuzzReplay replay, string artifactDirectory, string replayPath)
    {
        return new FuzzOptions
        {
            Targets = new[] { replay.Target }, Seed = replay.Seed, Count = replay.Count,
            Workers = 1, ArtifactDirectory = artifactDirectory, DurationReplay = replay.DurationBound,
            InputFile = replay.InputFile == null ? null : Path.GetFullPath(Path.Combine(Path.GetDirectoryName(replayPath)!, replay.InputFile)),
            ExpectedInputHash = replay.InputHash,
            ExpectedTraceHash = replay.FailureId == null ? replay.TraceHash : null
        };
    }

    internal static FuzzOptions FromCorpus(FuzzCorpusCase corpusCase, string artifactDirectory)
    {
        return new FuzzOptions
        {
            Targets = new[] { corpusCase.Target }, Seed = corpusCase.Seed, Count = corpusCase.Count,
            Workers = 1, ArtifactDirectory = artifactDirectory, ExpectedInputHash = corpusCase.InputHash,
            ExpectedTraceHash = corpusCase.TraceHash
        };
    }

    private static int Positive(string text, string name)
    {
        var value = int.Parse(text, CultureInfo.InvariantCulture);
        return value > 0 ? value : throw new ArgumentOutOfRangeException(name);
    }

    private static TimeSpan ParseDuration(string text)
    {
        const string message = "Duration must be a TimeSpan containing ':' or end in s, m, h, or d.";
        if (string.IsNullOrWhiteSpace(text)) throw new FormatException(message);
        if (text.Contains(':') && TimeSpan.TryParse(text, CultureInfo.InvariantCulture, out var value) && value > TimeSpan.Zero)
            return value;
        if (text.Length < 2) throw new FormatException(message);
        var suffix = text[^1];
        if (!double.TryParse(text[..^1], NumberStyles.Float, CultureInfo.InvariantCulture, out var number) || number <= 0)
            throw new FormatException(message);
        try
        {
            return suffix switch
            {
                's' => TimeSpan.FromSeconds(number), 'm' => TimeSpan.FromMinutes(number),
                'h' => TimeSpan.FromHours(number), 'd' => TimeSpan.FromDays(number),
                _ => throw new FormatException(message)
            };
        }
        catch (OverflowException) { throw new FormatException(message); }
    }
}

internal sealed class HelpRequestedException : Exception;
