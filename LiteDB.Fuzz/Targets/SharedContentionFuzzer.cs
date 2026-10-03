using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using LiteDB.Tests.Safety;
using LiteDB.Utils;

namespace LiteDB.Fuzz.Targets;

/// <summary>
/// Two to four real processes on one Shared-mode database with an alternating writer: each child
/// (<see cref="SharedContentionChild"/>) repeatedly takes writer ownership with an explicit
/// transaction in its own id range (commit, sometimes rollback), with occasional auto-commit writes
/// and reads, all released together by a start barrier so they queue on the native writer mutex.
/// </summary>
/// <remarks>
/// Oracles. In each child: Deadline per operation (lock-bound default max(3 x TIMEOUT, 15 s) with
/// TIMEOUT = <see cref="Timeout"/>; the native writer wait itself is unbounded, so the deadline is the
/// only bound), Ownership after every operation, ConnectionClean after each dispose and Durable on a
/// fresh connection at its end. In the parent: every child join and the start barrier under a declared
/// deadline, then Durable over the union of all acknowledged ledgers on a cold reopen (plus the exact
/// collection), ConnectionClean and Quiescent at each round's end once every process exited. No fault
/// is injected, so FaultReached/FaultDisposed do not apply. Overtaking is measured, not asserted (see
/// <see cref="SharedContentionTimeline"/>). Evidence class 2 (native multi-process stress): the trace
/// holds only the seed-derived scripts; timings, PIDs and outcomes go to side files, and a failed round
/// keeps the children's ledgers, timings, output and database files plus <c>evidence.json</c>.
/// </remarks>
internal sealed class SharedContentionFuzzer : IFuzzTarget
{
    internal static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);
    private const string DatabaseName = "contention.db";
    private const string EvidenceFile = "evidence.json";
    // Declared harness bounds: every child is ready (process start, JIT, open) and finishes its whole
    // script plus its fresh-connection check (dozens of millisecond-long transactions) well within these.
    private static readonly TimeSpan ReadyDeadline = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan JoinDeadline = TimeSpan.FromSeconds(75);

    public string Name => "shared-contention";
    public string Description => "Two to four processes alternating Shared-mode writer ownership; overtaking measured.";

    public async Task RunAsync(FuzzContext context)
    {
        var database = context.RegisterFile(Path.Combine(context.DirectoryPath, DatabaseName));
        context.Oracles.PragmaTimeout = Timeout;
        context.Oracles.WatchOwnership();
        var timeline = new SharedContentionTimeline();
        var round = 0;
        var processes = 0;
        while (context.Next())
        {
            round++;
            var workers = 2 + Math.Abs((context.Seed + round) % 3);
            var steps = Math.Clamp(context.Count, 24, 64);
            var seeds = Enumerable.Range(0, workers).Select(worker => unchecked(context.Seed + round * 397 + worker * 7919)).ToArray();
            context.Trace("round", new { round, workers, steps });
            for (var worker = 0; worker < workers; worker++)
                context.Trace("child-script", new
                {
                    round, worker, seed = seeds[worker],
                    script = SharedContentionScript.Generate(seeds[worker], worker, steps).Select(step => step.ToString()).ToArray()
                });
            WriteEvidence(context, round, seeds, steps, "running", null);
            try { await RunRoundAsync(context, database, round, seeds, steps, timeline); }
            catch (Exception error)
            {
                WriteEvidence(context, round, seeds, steps, "failed", error);
                throw;
            }
            processes += workers;
        }
        File.Delete(Path.Combine(context.DirectoryPath, EvidenceFile));
        context.Metrics["rounds"] = round;
        context.Metrics["processes"] = processes;
        timeline.WriteMetrics(context.Metrics);
    }

    private static async Task RunRoundAsync(FuzzContext context, string database, int round, int[] seeds, int steps,
        SharedContentionTimeline timeline)
    {
        // The previous round passed (or this is the first): its files are no longer evidence.
        foreach (var stale in Directory.EnumerateFiles(context.DirectoryPath, "worker-*")
            .Concat(Directory.EnumerateFiles(context.DirectoryPath, "contention*")).ToArray())
            File.Delete(stale);
        var seedDb = new LiteDatabase(OpenEngine(database));
        using (seedDb)
        {
            context.Deadline("Seed", () =>
            {
                seedDb.Timeout = Timeout;
                seedDb.GetCollection(SharedContentionScript.Collection).Insert(Control());
            }, "mode=shared;parent");
            context.Ownership(seedDb, "after seeding");
        }
        context.ConnectionClean(seedDb, "SeedDispose");

        var children = new List<(Process Process, int Worker, Task<string> Output, Task<string> Error)>();
        void KillChildren(object sender, EventArgs args)
        {
            // An overdue parent operation exits the process: no child may outlive it holding the mutex.
            foreach (var child in children.Where(child => !child.Process.HasExited)) child.Process.Kill(entireProcessTree: true);
        }
        AppDomain.CurrentDomain.ProcessExit += KillChildren;
        try
        {
            for (var worker = 0; worker < seeds.Length; worker++)
            {
                var process = Process.Start(StartInfo(database, SharedContentionScript.Ledger(context.DirectoryPath, worker),
                    worker, seeds[worker], steps)) ?? throw new InvalidOperationException("Failed to start a shared-contention child.");
                children.Add((process, worker, process.StandardOutput.ReadToEndAsync(), process.StandardError.ReadToEndAsync()));
                // PIDs change on every replay: diagnostics only, never in trace.jsonl.
                File.AppendAllText(Path.Combine(context.DirectoryPath, "processes.jsonl"),
                    System.Text.Json.JsonSerializer.Serialize(new { round, worker, pid = process.Id, seed = seeds[worker] }) + "\n");
            }
            var dimension = $"mode=shared;processes={seeds.Length}";
            await context.DeadlineAsync("AwaitChildrenReady", async () =>
            {
                while (!children.All(child => File.Exists(SharedContentionScript.Ready(SharedContentionScript.Ledger(context.DirectoryPath, child.Worker)))))
                {
                    // A child that exited before it was ready is reported with its own failure below.
                    if (children.Any(child => child.Process.HasExited)) return;
                    await Task.Delay(5);
                }
            }, dimension, ReadyDeadline);
            // All children are open and waiting: real progress, and the joins start their own clocks.
            context.PulseHeartbeat();
            File.WriteAllText(SharedContentionScript.Go(SharedContentionScript.Ledger(context.DirectoryPath, 0)), "go");
            var joins = children.Select(child => context.DeadlineAsync("JoinChild",
                () => child.Process.WaitForExitAsync(), $"{dimension};worker={child.Worker}", JoinDeadline)).ToArray();
            await Task.WhenAll(joins);
        }
        finally { AppDomain.CurrentDomain.ProcessExit -= KillChildren; }

        var records = new List<ContentionAcquisition>();
        var failures = new List<(string Id, string Message, int Rank, long AtTicks)>();
        foreach (var child in children)
        {
            var ledger = SharedContentionScript.Ledger(context.DirectoryPath, child.Worker);
            File.WriteAllText(Path.ChangeExtension(ledger, ".stdout.txt"), await child.Output);
            File.WriteAllText(Path.ChangeExtension(ledger, ".stderr.txt"), await child.Error);
            var exitCode = child.Process.ExitCode;
            child.Process.Dispose();
            var failure = SharedContentionScript.Failure(ledger);
            if (File.Exists(failure))
            {
                var reported = System.Text.Json.JsonSerializer.Deserialize<ContentionChildFailure>(File.ReadAllText(failure));
                failures.Add((reported.FailureId, $"worker {child.Worker} exited {exitCode}: {reported.Message}", reported.Rank, reported.AtTicks));
            }
            else if (exitCode != 0 || !File.Exists(SharedContentionScript.Result(ledger)))
                failures.Add(("SHARED_CONTENTION_CHILD_EXIT", $"worker {child.Worker} exited {exitCode} without a failure record: {await child.Error}", 3, long.MaxValue));
            else
            {
                using var result = JsonDocument.Parse(File.ReadAllText(SharedContentionScript.Result(ledger)));
                if (result.RootElement.GetProperty("stopwatchFrequency").GetInt64() != Stopwatch.Frequency)
                    failures.Add(("SHARED_CONTENTION_CLOCK", $"worker {child.Worker} reports another Stopwatch frequency.", 3, long.MaxValue));
                // The children's own oracle checks, so the run shows they ran.
                foreach (var name in new[] { "ownershipChecks", "commits", "rollbacks", "autoWrites", "reads" })
                {
                    var key = "child" + char.ToUpperInvariant(name[0]) + name[1..];
                    context.Metrics[key] = (context.Metrics.TryGetValue(key, out var sum) ? (int)sum : 0) +
                        result.RootElement.GetProperty(name).GetInt32();
                }
            }
            if (File.Exists(SharedContentionScript.Timing(ledger)))
                records.AddRange(File.ReadLines(SharedContentionScript.Timing(ledger))
                    .Select(line => new ContentionAcquisition(round, child.Worker, System.Text.Json.JsonSerializer.Deserialize<ContentionTimingLine>(line))));
        }
        if (failures.Count > 0)
        {
            // The primary failure first (see ContentionChildFailure.Rank): a stalled owner, not the peers waiting for it.
            var ordered = failures.OrderBy(item => item.Rank).ThenBy(item => item.AtTicks).ToArray();
            throw new FuzzFailureException(ordered[0].Id, string.Join(" | ", ordered.Select(item => $"{item.Id}: {item.Message}")));
        }

        var situations = timeline.AddRound(context.DirectoryPath, round, records);
        if (situations.WaitedForPeer > 0) Reachability.Sometimes("situation:shared-contention-writer-waited-for-peer-process");
        if (situations.RollbackHandoffs > 0) Reachability.Sometimes("situation:shared-contention-rollback-under-contention");

        // Every child exited: a cold reopen shows the union of all acknowledged ledgers and nothing else.
        var union = new DurableLedger();
        var exact = new SortedDictionary<int, BsonDocument> { [SharedContentionScript.ControlId] = Control() };
        union.Acknowledge(SharedContentionScript.Collection, SharedContentionScript.ControlId, Control());
        for (var worker = 0; worker < seeds.Length; worker++)
        {
            var ledger = SharedContentionScript.Ledger(context.DirectoryPath, worker);
            if (!File.Exists(ledger)) continue;
            foreach (var line in File.ReadLines(ledger).Select(text => System.Text.Json.JsonSerializer.Deserialize<ContentionLedgerLine>(text)))
            {
                var document = line.Op == "upsert" ? SharedContentionScript.Document(line.Id, line.Value, worker) : null;
                if (line.Op == "rollback") union.Abort(SharedContentionScript.Collection, line.Id, null);
                else union.Acknowledge(SharedContentionScript.Collection, line.Id, document);
                if (line.Op == "upsert") exact[line.Id] = document;
                else if (line.Op == "delete") exact.Remove(line.Id);
            }
        }
        union.ExpectExactly(SharedContentionScript.Collection, exact.Values);
        var cold = new LiteDatabase(OpenEngine(database));
        using (cold)
        {
            context.Deadline("ColdReopen", () => context.Durable(union, cold, $"round {round}: every child exited, cold reopen"),
                "mode=shared;parent");
            context.Ownership(cold, "after the cold reopen");
            // The raw page check below reads the data file: fold the WAL in first.
            context.Deadline("Checkpoint", () => cold.Checkpoint(), "mode=shared;parent");
        }
        context.ConnectionClean(cold, "ColdReopenDispose");
        DatabaseIntegrityVerifier.Verify(context, database);
        context.Quiescent(database, $"round {round} end");
        context.Trace("round-verified", new { round, documents = exact.Count });
    }

    internal static SharedEngine OpenEngine(string database) =>
        new(new Engine.EngineSettings { Filename = database, DurableCommits = true });

    private static BsonDocument Control() => new() { ["_id"] = SharedContentionScript.ControlId, ["value"] = "control" };

    private static ProcessStartInfo StartInfo(string database, string ledger, int worker, int seed, int steps)
    {
        var start = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        start.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
        Add("--child", "shared-contention"); Add("--database", database); Add("--ledger", ledger);
        Add("--worker-id", worker.ToString()); Add("--seed", seed.ToString()); Add("--count", steps.ToString());
        return start;

        void Add(string name, string value) { start.ArgumentList.Add(name); start.ArgumentList.Add(value); }
    }

    /// <summary>
    /// <c>evidence.json</c>: written when a round starts (so a hard exit still leaves it), rewritten
    /// with the failure, deleted when every round passed. Class 2: a failure that does not reproduce on
    /// replay is still a finding and is classified before any conclusion.
    /// </summary>
    private static void WriteEvidence(FuzzContext context, int round, int[] seeds, int steps, string outcome, Exception error) =>
        File.WriteAllText(Path.Combine(context.DirectoryPath, EvidenceFile), System.Text.Json.JsonSerializer.Serialize(new
        {
            evidenceClass = 2, target = context.Target, runSeed = context.Seed, round, workerCount = seeds.Length, seeds, steps,
            outcome, failureId = error == null ? null : FailureIdentity.Get(error), message = error?.Message,
            environment = new
            {
                os = RuntimeInformation.OSDescription, architecture = RuntimeInformation.OSArchitecture.ToString(),
                cpuCount = Environment.ProcessorCount, runtime = RuntimeInformation.FrameworkDescription,
                stopwatchFrequency = Stopwatch.Frequency
            },
            retained = "worker-<k>.jsonl (acknowledged ledger), .timing.jsonl, .failure.json, .stdout.txt, .stderr.txt, " +
                "processes.jsonl, acquisitions.jsonl, contention.db and its companions",
            nonReproduction = "classify before concluding: schedule-dependent | environment-dependent | harness nondeterminism"
        }, new JsonSerializerOptions { WriteIndented = true }));
}
