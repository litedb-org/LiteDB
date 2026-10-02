using LiteDB.Tests.Mapper;

namespace LiteDB.Fuzz.Targets;

/// <summary>One write of a <c>shared-contention</c> child step: an upsert of <see cref="Value"/> or a delete.</summary>
internal sealed record ContentionWrite(bool Delete, int Id, int Value);

/// <summary>
/// One step of a child's script: an explicit transaction (<c>BeginTrans</c>, writes, then <c>Commit</c>
/// or <c>Rollback</c>), an auto-commit upsert, or a read of one of the child's own ids.
/// </summary>
internal sealed record ContentionStep(string Kind, ContentionWrite[] Writes, bool Rollback, int ReadId)
{
    internal const string Transaction = "transaction";
    internal const string Auto = "auto";
    internal const string Read = "read";

    /// <summary>Compact, deterministic text of the step for <c>trace.jsonl</c>.</summary>
    public override string ToString() => this.Kind switch
    {
        Transaction => $"tx({string.Join(",", this.Writes.Select(Text))}){(this.Rollback ? "rollback" : "commit")}",
        Auto => $"auto({string.Join(",", this.Writes.Select(Text))})",
        _ => $"read({this.ReadId})"
    };

    private static string Text(ContentionWrite write) => (write.Delete ? "d" : "u") + write.Id;
}

/// <summary>
/// The deterministic part of <c>shared-contention</c>: each child's operation script derives only
/// from its seed (<see cref="StableRandom"/>), so the parent can trace exactly what the child runs.
/// Also the names of the per-child files (one directory per run, prefix <c>worker-&lt;k&gt;</c>):
/// <list type="bullet">
/// <item><c>worker-k.jsonl</c> ledger, append-only, one line per write after its
/// <c>Commit</c>/auto-commit returned (<c>{"Op":"upsert|delete","Id","Value"}</c>) or after its
/// <c>Rollback</c> returned (<c>{"Op":"rollback","Id","Value":0}</c>).</item>
/// <item><c>worker-k.timing.jsonl</c>, one line per explicit writer acquisition:
/// <c>{"Step","Rollback","Arrive","Acquired","Released"}</c> in <see cref="System.Diagnostics.Stopwatch"/> ticks.</item>
/// <item><c>worker-k.ready</c> (connection open), <c>worker-k.result.json</c> (counts at a clean exit),
/// <c>worker-k.failure.json</c> (failure id, message, in-flight operations), and the parent's
/// <c>worker-k.stdout.txt</c>/<c>.stderr.txt</c>.</item>
/// </list>
/// </summary>
internal static class SharedContentionScript
{
    internal const string Collection = "rows";
    internal const int ControlId = 1;
    private const int KeysPerWorker = 40;

    internal static int FirstId(int worker) => 10_000 + worker * 100_000;

    internal static List<ContentionStep> Generate(int seed, int worker, int steps)
    {
        var random = new StableRandom(seed);
        var script = new List<ContentionStep>(steps);
        for (var step = 0; step < steps; step++)
        {
            var kind = random.Next(10);
            if (kind < 7)
            {
                var writes = Enumerable.Range(0, 1 + random.Next(4)).Select(_ => Write(random, worker, random.Next(4) == 0)).ToArray();
                script.Add(new ContentionStep(ContentionStep.Transaction, writes, random.Next(5) == 0, 0));
            }
            else if (kind < 9)
            {
                script.Add(new ContentionStep(ContentionStep.Auto, new[] { Write(random, worker, false) }, false, 0));
            }
            else
            {
                script.Add(new ContentionStep(ContentionStep.Read, Array.Empty<ContentionWrite>(), false,
                    FirstId(worker) + random.Next(1, KeysPerWorker)));
            }
        }
        return script;
    }

    internal static BsonDocument Document(int id, int value, int worker) =>
        new() { ["_id"] = id, ["value"] = value, ["worker"] = worker };

    internal static string Ledger(string directory, int worker) => Path.Combine(directory, $"worker-{worker}.jsonl");
    internal static string Timing(string ledger) => Path.ChangeExtension(ledger, ".timing.jsonl");
    internal static string Ready(string ledger) => Path.ChangeExtension(ledger, ".ready");
    internal static string Result(string ledger) => Path.ChangeExtension(ledger, ".result.json");
    internal static string Failure(string ledger) => Path.ChangeExtension(ledger, ".failure.json");
    internal static string Go(string ledger) => Path.Combine(Path.GetDirectoryName(ledger), "contention.go");

    private static ContentionWrite Write(StableRandom random, int worker, bool delete) =>
        new(delete, FirstId(worker) + random.Next(1, KeysPerWorker), delete ? 0 : random.Next());
}

/// <summary>A ledger line of a child (see <see cref="SharedContentionScript"/>).</summary>
internal sealed record ContentionLedgerLine(string Op, int Id, int Value);

/// <summary>A timing line of a child: one explicit writer acquisition, in Stopwatch ticks.</summary>
internal sealed record ContentionTimingLine(int Step, bool Rollback, long Arrive, long Acquired, long Released);

/// <summary>
/// What a child writes to <c>worker-k.failure.json</c> before it exits non-zero. <see cref="OwnedWriter"/>:
/// the child owned writer ownership (between BeginTrans and Commit/Rollback returning) when it failed.
/// <see cref="AtTicks"/>: when the failure began (an overdue operation's start, else its detection).
/// </summary>
internal sealed record ContentionChildFailure(string FailureId, string Message, int Worker, int Seed, int Step,
    string Op, double ElapsedMs, double DeadlineMs, bool OwnedWriter, long AtTicks, object[] InFlight)
{
    /// <summary>
    /// The parent reports the lowest rank first. Only one process owns the writer, and peers queued in
    /// BeginTrans overrun their deadlines because of a stalled owner (they also started waiting before
    /// it acquired, so start time alone would blame them): the owner first, then any operation other
    /// than a BeginTrans wait, then the earliest.
    /// </summary>
    internal int Rank => this.OwnedWriter ? 0 : this.Op == "BeginTrans" ? 2 : 1;
}
