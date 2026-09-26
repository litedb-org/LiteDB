using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using LiteDB.Tests.Mapper;

namespace LiteDB.Fuzz.Targets;

internal sealed class SharedProcessFuzzer : IFuzzTarget
{
    private const int OuterCrashBoundaries = 12;
    private static readonly string[] InternalCrashPoints =
    {
        "wal-page-before-write", "wal-page-after-write",
        "wal-confirmation-before-write", "wal-confirmation-after-write",
        "wal-before-durable-flush", "wal-after-durable-flush",
        "wal-before-index-confirmation", "wal-after-index-confirmation",
        "checkpoint-before-page-write", "checkpoint-after-page-write",
        "checkpoint-before-data-flush", "checkpoint-after-data-flush",
        "checkpoint-before-clear", "checkpoint-after-clear"
    };
    private static int CrashBoundaryCount => OuterCrashBoundaries + InternalCrashPoints.Length;

    public string Name => "shared";
    public string Description => "Real child-process shared-mode transactions, checkpoints, reopen, and owner death.";

    public async Task RunAsync(FuzzContext context)
    {
        var database = context.RegisterFile(Path.Combine(context.DirectoryPath, "shared.db"));
        var rounds = 0;
        var totalProcesses = 0;
        var acknowledgedRows = 0;
        var observedCrashPositions = new HashSet<int>();
        var integrityChecks = 0;
        while (context.Next())
        {
            rounds++;
            foreach (var oldLedger in Directory.EnumerateFiles(context.DirectoryPath, "worker-*"))
                File.Delete(oldLedger);
            File.Delete(database);
            File.Delete(Path.ChangeExtension(database, null) + "-log.db");
            var crashPosition = (rounds - 1) % CrashBoundaryCount + 1;
            using (var seed = Open(database))
            {
                ConfigureCrashCheckpoint(seed, crashPosition);
                seed.GetCollection("rows").Insert(new BsonDocument { ["_id"] = 1, ["value"] = "control" });
            }

            var workerCount = 2 + Math.Abs((context.Seed + rounds) % 3);
            var operations = Math.Max(12, context.Count);
            var processes = new List<(Process Process, string Ledger, bool ExpectedCrash, int Worker)>();
            for (var worker = 0; worker < workerCount; worker++)
            {
                var ledger = Path.Combine(context.DirectoryPath, $"worker-{worker}.jsonl");
                var crashAt = worker == 0 ? crashPosition : -1;
                if (crashAt > 0) context.ObserveNovelty("shared-crash-boundary", crashAt, workerCount);
                var process = Process.Start(StartInfo(database, ledger, worker,
                    context.Seed + rounds * 397 + worker, operations, crashAt))
                    ?? throw new InvalidOperationException("Failed to start shared-mode child process.");
                processes.Add((process, ledger, worker == 0, worker));
                // Native PIDs are useful diagnostics, but change on every replay and
                // cannot participate in the corpus's deterministic trace contract.
                File.AppendAllText(Path.Combine(context.DirectoryPath, "processes.jsonl"),
                    System.Text.Json.JsonSerializer.Serialize(new { round = rounds, worker, process.Id }) + Environment.NewLine);
                context.Trace("spawn", new { round = rounds, worker, crashAt });
            }

            foreach (var child in processes)
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
                try { await child.Process.WaitForExitAsync(timeout.Token); }
                catch (OperationCanceledException)
                {
                    child.Process.Kill(entireProcessTree: true);
                    throw new FuzzFailureException($"Shared worker {child.Process.Id} hung for 90 seconds.");
                }
                var output = await child.Process.StandardOutput.ReadToEndAsync();
                var error = await child.Process.StandardError.ReadToEndAsync();
                context.Trace("child-exit", new { round = rounds, worker = child.Worker, child.Process.ExitCode, output, error });
                if (!child.ExpectedCrash) context.Check(child.Process.ExitCode == 0,
                    $"Shared worker {child.Process.Id} exited {child.Process.ExitCode}: {error}");
                else
                {
                    context.Check(child.Process.ExitCode != 0, "Crash worker unexpectedly completed normally.");
                    var marker = CrashMarker(child.Ledger);
                    context.Check(File.Exists(marker),
                        "Shared crash worker exited without proving the configured hook fired: " + error);
                    var observed = File.ReadAllText(marker);
                    var position = int.Parse(observed.Split('|')[0]);
                    FuzzOracle.VerifyCrashMarker(context, observed, ExpectedMarker(position));
                    observedCrashPositions.Add(position);
                }
                child.Process.Dispose();
            }

            var expected = new SortedDictionary<int, int>();
            var optional = new List<LedgerOperation[]>();
            foreach (var child in processes)
            {
                if (File.Exists(child.Ledger))
                {
                    Apply(expected, ReadLedger(child.Ledger));
                }
                else if (child.ExpectedCrash && File.Exists(Intent(child.Ledger)))
                {
                    optional.Add(ReadLedger(Intent(child.Ledger)));
                }
            }

            using var verify = Open(database);
            var actual = verify.GetCollection("rows").FindAll().ToArray();
            context.Check(actual.Any(row => row["_id"].AsInt32 == 1 && row["value"].AsString == "control"),
                "Owner death lost the control transaction.");
            var workers = actual.Where(row => row["_id"].AsInt32 != 1)
                .ToDictionary(row => row["_id"].AsInt32, row => row["value"].AsInt32);
            foreach (var group in optional)
            {
                var present = group.Count(operation => workers.TryGetValue(operation.Id, out var value) && value == operation.Value);
                context.Check(present == 0 || present == group.Length,
                    "Owner death exposed a partially committed transaction.");
                if (present == group.Length) Apply(expected, group);
            }
            context.Check(expected.Count == workers.Count && expected.All(pair => workers.TryGetValue(pair.Key, out var value) && value == pair.Value),
                "Recovered shared-mode state disagreed with acknowledged child ledgers.");
            verify.Checkpoint();
            DatabaseIntegrityVerifier.Verify(context, database);
            integrityChecks++;
            totalProcesses += workerCount;
            acknowledgedRows += expected.Count;
        }
        if (rounds >= CrashBoundaryCount)
            context.Check(observedCrashPositions.Count == CrashBoundaryCount,
                "Shared campaign did not observe every owner-death boundary.");
        context.Metrics["rounds"] = rounds;
        context.Metrics["processes"] = totalProcesses;
        context.Metrics["acknowledgedRows"] = acknowledgedRows;
        context.Metrics["observedCrashPositions"] = observedCrashPositions.Count;
        context.Metrics["observedInternalCrashPoints"] = observedCrashPositions.Count(position => position > OuterCrashBoundaries);
        context.Metrics["integrityChecks"] = integrityChecks;
    }

    /// <summary>
    /// Checkpoint faults must run before Commit gives up writer ownership. Otherwise a
    /// peer can drain the WAL between Commit and the explicit Checkpoint, leaving no
    /// work that reaches the selected hook. Ordinary and WAL-fault rounds keep defaults.
    /// </summary>
    internal static void ConfigureCrashCheckpoint(LiteDatabase database, int position)
    {
        if (position > OuterCrashBoundaries &&
            InternalCrashPoints[position - OuterCrashBoundaries - 1].StartsWith("checkpoint-", StringComparison.Ordinal))
            database.CheckpointSize = 1;
    }

    internal static int RunChild(FuzzOptions options)
    {
        var random = new StableRandom(options.Seed);
        var crashPoint = options.CrashAt > OuterCrashBoundaries
            ? InternalCrashPoints[options.CrashAt - OuterCrashBoundaries - 1] : null;
        if (crashPoint != null)
        {
            Engine.EngineState.SimulateProcessCrash = phase =>
            {
                if (phase != crashPoint) return;
                WriteCrashMarker(options.Ledger, options.CrashAt, phase);
                Environment.FailFast($"Deterministic internal process death at {phase}");
            };
        }
        using var db = Open(options.Database, out var engine);
        using var checkpointScope = SharedCheckpointCrashScope.Create(engine, crashPoint);
        var rows = db.GetCollection("rows");
        if (options.CrashAt > 0)
        {
            Crash(1, "open");
            db.BeginTrans();
            Crash(2, "begin");
            var operations = new List<LedgerOperation>();
            for (var batch = 0; batch < 4; batch++)
            {
                var id = 10_000 + options.WorkerId * 100_000 + batch + 1;
                var value = random.Next();
                rows.Upsert(new BsonDocument { ["_id"] = id, ["value"] = value, ["worker"] = options.WorkerId });
                operations.Add(new LedgerOperation("upsert", id, value));
                Crash(3 + batch, "write-" + (batch + 1));
            }
            WriteLedger(Intent(options.Ledger), operations);
            Crash(7, "before-completion");
            Crash(8, "before-commit");
            db.Commit();
            WriteLedger(options.Ledger, operations);
            Crash(9, "commit-confirmed");
            Crash(10, "before-checkpoint");
            db.Checkpoint();
            Crash(11, "checkpoint");
            Crash(12, "before-close");
            throw new InvalidOperationException("Configured shared-process crash boundary was not reached.");
        }

        for (var step = 0; step < options.Count;)
        {
            db.BeginTrans();
            var operations = new List<LedgerOperation>();
            for (var batch = 0; batch < 4 && step < options.Count; batch++, step++)
            {
                var id = 10_000 + options.WorkerId * 100_000 + random.Next(1, 80);
                var value = random.Next();
                if (random.Next(4) == 0)
                {
                    rows.Delete(id);
                    operations.Add(new LedgerOperation("delete", id, 0));
                }
                else
                {
                    rows.Upsert(new BsonDocument { ["_id"] = id, ["value"] = value, ["worker"] = options.WorkerId });
                    operations.Add(new LedgerOperation("upsert", id, value));
                }
            }
            if (random.Next(5) == 0)
            {
                db.Rollback();
            }
            else
            {
                db.Commit();
                foreach (var operation in operations)
                    File.AppendAllText(options.Ledger, System.Text.Json.JsonSerializer.Serialize(operation) + Environment.NewLine);
            }
            if (random.Next(7) == 0)
            {
                db.Checkpoint();
            }
        }
        return 0;

        void Crash(int position, string phase)
        {
            if (position == options.CrashAt)
            {
                WriteCrashMarker(options.Ledger, position, phase);
                Environment.FailFast($"Deterministic shared-mode owner death at {phase} boundary {position}");
            }
        }
    }

    private static LiteDatabase Open(string database) => Open(database, out _);

    private static LiteDatabase Open(string database, out SharedEngine engine)
    {
        engine = new SharedEngine(new Engine.EngineSettings
        {
            Filename = database, DurableCommits = true, TransactionPageLimit = 4
        });
        return new LiteDatabase(engine);
    }

    private static ProcessStartInfo StartInfo(string database, string ledger, int worker, int seed, int count, int crashAt)
    {
        var start = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false
        };
        start.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
        Add("--child", "shared"); Add("--database", database); Add("--ledger", ledger);
        Add("--worker-id", worker.ToString()); Add("--seed", seed.ToString()); Add("--count", count.ToString());
        Add("--crash-at", crashAt.ToString());
        return start;

        void Add(string name, string value) { start.ArgumentList.Add(name); start.ArgumentList.Add(value); }
    }

    private static string Intent(string ledger) => ledger + ".intent";
    private static string CrashMarker(string ledger) => ledger + ".crash";

    private static string ExpectedMarker(int position) => position > OuterCrashBoundaries
        ? $"{position}|{InternalCrashPoints[position - OuterCrashBoundaries - 1]}"
        : $"{position}|{new[] { "", "open", "begin", "write-1", "write-2", "write-3", "write-4",
            "before-completion", "before-commit", "commit-confirmed", "before-checkpoint", "checkpoint", "before-close" }[position]}";

    private static void WriteCrashMarker(string ledger, int position, string phase)
    {
        using var stream = new FileStream(CrashMarker(ledger), FileMode.Create, FileAccess.Write, FileShare.Read);
        using var writer = new StreamWriter(stream);
        writer.Write($"{position}|{phase}");
        writer.Flush();
        stream.Flush(true);
    }

    private static LedgerOperation[] ReadLedger(string path) => File.ReadLines(path)
        .Select(line => System.Text.Json.JsonSerializer.Deserialize<LedgerOperation>(line)).ToArray();

    private static void WriteLedger(string path, IEnumerable<LedgerOperation> operations)
    {
        foreach (var operation in operations)
            File.AppendAllText(path, System.Text.Json.JsonSerializer.Serialize(operation) + Environment.NewLine);
    }

    private static void Apply(IDictionary<int, int> model, IEnumerable<LedgerOperation> operations)
    {
        foreach (var operation in operations)
        {
            if (operation.Operation == "delete") model.Remove(operation.Id);
            else model[operation.Id] = operation.Value;
        }
    }

    private sealed record LedgerOperation(string Operation, int Id, int Value);
}
