using System.Collections.Concurrent;
using LiteDB.Tests.Safety;
using LiteDB.Utils;

namespace LiteDB.Fuzz.Targets;

/// <remarks>
/// Oracles: every public operation runs under <see cref="FuzzOracles.Deadline{T}"/> on the thread
/// that issues it (explicit transactions belong to that thread); joining the workers has its own
/// declared deadline, so a worker that never arrives is reported. Unique-key losers must fail with
/// the duplicate-key error and nothing else. Each dispose is followed by
/// <see cref="FuzzOracles.ConnectionClean"/>, the scenario end by <see cref="FuzzOracles.Quiescent"/>,
/// and a cold reopen checks <see cref="FuzzOracles.Durable"/> for the acknowledged counter and unique
/// winners. No fault is injected, so FaultReached/FaultDisposed do not apply; Ownership does not
/// apply to this Direct-mode connection.
/// </remarks>
internal sealed class ConcurrentFuzzer : IFuzzTarget
{
    private const string Dimension = "mode=direct;threads=4";
    // Declared deadlines beyond the lock-bound default: a rebuild, and joining four workers whose
    // operations may each wait out the lock timeout in turn.
    private static readonly TimeSpan RebuildDeadline = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan JoinDeadline = TimeSpan.FromSeconds(60);

    public string Name => "concurrent";
    public string Description => "Same-LiteDatabase thread contention, transactions, unique keys, cursors, and checkpoints.";

    public async Task RunAsync(FuzzContext context)
    {
        var file = context.RegisterFile(Path.Combine(context.DirectoryPath, "concurrent.db"));
        var ledger = new DurableLedger();
        var db = new LiteDatabase(new ConnectionString
        {
            Filename = file, TransactionPageLimit = 3
        });
        using (db) await RunAsync(context, db, file, ledger);
        context.ConnectionClean(db);
        var cold = new LiteDatabase(file);
        using (cold) context.Durable(ledger, cold, "close and cold reopen");
        context.ConnectionClean(cold, "ColdReopenDispose");
        context.Quiescent(file);
    }

    private static async Task RunAsync(FuzzContext context, LiteDatabase db, string file, DurableLedger ledger)
    {
        db.Timeout = TimeSpan.FromSeconds(10);
        context.Oracles.PragmaTimeout = db.Timeout;
        db.CheckpointSize = 0;
        var rows = db.GetCollection("rows");
        rows.EnsureIndex("unique", "Unique", true);
        rows.Insert(new BsonDocument { ["_id"] = 1, ["Counter"] = 0, ["Unique"] = "control" });
        var expectedCounter = 0;
        var uniqueWinners = 0;
        var overlaps = 0;

        while (context.Next())
        {
            var snapshotCounter = context.Deadline("FindById", () => rows.FindById(1), Dimension)["Counter"].AsInt32;
            using var cursor = rows.Query().OrderBy("_id").ToEnumerable().GetEnumerator();
            context.Check(context.Deadline("CursorOpen", cursor.MoveNext, Dimension), "Concurrent reader could not pin its snapshot.");
            using var start = new Barrier(5);
            var committed = new ConcurrentBag<int>();
            var failures = new ConcurrentQueue<Exception>();
            var tasks = Enumerable.Range(0, 4).Select(worker => Task.Factory.StartNew(() =>
            {
                try
                {
                    context.Deadline("Transaction", () =>
                    {
                        start.SignalAndWait();
                        context.Check(db.BeginTrans(), "Concurrent worker could not begin a transaction.");
                        var changed = db.GetCollection("rows").UpdateMany("{ Counter: Counter + 1 }", "_id = 1");
                        context.Check(changed == 1, "Concurrent counter update changed the wrong row count.");
                        if (!db.Commit()) throw new InvalidOperationException("Concurrent commit returned false.");
                    }, Dimension);
                    committed.Add(worker);
                }
                catch (Exception error)
                {
                    try { db.Rollback(); } catch { }
                    failures.Enqueue(error);
                }
            }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default)).ToArray();
            context.Deadline("StartWorkers", () => start.SignalAndWait(), Dimension);
            // The loop keeps its async continuations: they move it between pool threads, which is part of the scenario.
            await context.DeadlineAsync("JoinTransactionWorkers", () => Task.WhenAll(tasks), Dimension, JoinDeadline);
            context.Check(failures.IsEmpty, "Concurrent transaction failed: " +
                string.Join(" | ", failures.Select(error => error.Message)));
            expectedCounter += committed.Count;
            context.Check(committed.Count == 4, "Not every same-document transaction committed.");
            Reachability.Sometimes("situation:concurrent-same-document-commits");

            var key = "round-" + context.Steps;
            var contenders = new ConcurrentBag<int>();
            var losers = new ConcurrentBag<int>();
            var unexpected = new ConcurrentQueue<Exception>();
            var conflictTasks = Enumerable.Range(0, 4).Select(worker => Task.Run(() =>
            {
                var id = 10_000 + context.Steps * 10 + worker;
                try
                {
                    var document = new BsonDocument { ["_id"] = id, ["Unique"] = key, ["Counter"] = worker };
                    context.Deadline("Insert", () => db.GetCollection("rows").Insert(document), Dimension);
                    contenders.Add(id);
                    ledger.Acknowledge("rows", id, document);
                }
                // A loser must fail on the unique index and nothing else: any other error is a finding.
                catch (LiteException error) when (error.ErrorCode == LiteException.INDEX_DUPLICATE_KEY) { losers.Add(id); }
                catch (Exception error) { unexpected.Enqueue(error); }
            })).ToArray();
            await context.DeadlineAsync("JoinUniqueContenders", () => Task.WhenAll(conflictTasks), Dimension, JoinDeadline);
            context.Check(unexpected.IsEmpty, "Unique-key contention failed with an unexpected error: " +
                string.Join(" | ", unexpected.Select(error => error.GetType().Name + ": " + error.Message)));
            context.Check(contenders.Count == 1, "Unique-key contention did not have exactly one winner.");
            context.Check(losers.Count == 3, "Not every unique-key loser failed with the duplicate-key error.");
            uniqueWinners++;

            Task checkpoint;
            using (ExecutionContext.SuppressFlow())
                checkpoint = Task.Factory.StartNew(() => context.Deadline("Checkpoint", db.Checkpoint, Dimension),
                    CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
            var observed = new List<BsonDocument> { Clone(cursor.Current) };
            context.Deadline("CursorDrain", () => { while (cursor.MoveNext()) observed.Add(Clone(cursor.Current)); }, Dimension);
            context.Check(observed.Single(document => document["_id"] == 1)["Counter"] == snapshotCounter,
                "Concurrent long cursor observed a later counter version.");
            context.Check(await Completes(checkpoint, TimeSpan.FromSeconds(15)),
                "Checkpoint did not finish after the concurrent cursor drained.");
            Reachability.Sometimes("situation:concurrent-checkpoint-under-cursor");
            context.Check(rows.FindById(1)["Counter"] == expectedCounter,
                "Final counter disagreed with acknowledged concurrent commits.");
            context.Check(rows.Count(BsonExpression.Create("Unique = @0", key)) == 1,
                "Unique contention left zero or multiple indexed rows.");
            overlaps += committed.Count;

            if (context.Steps % 5 == 0)
            {
                context.Deadline("Rebuild", () => db.Rebuild(), Dimension, RebuildDeadline);
                rows = db.GetCollection("rows");
                context.Check(rows.FindById(1)["Counter"] == expectedCounter,
                    "Rebuild after contention changed the acknowledged counter.");
            }
            context.ObserveNovelty("same-process-contention", committed.Count, contenders.Count,
                context.Steps % 5 == 0);
        }
        db.Checkpoint();
        DatabaseIntegrityVerifier.Verify(context, file);
        // The counter row's acknowledged state is the sum of every committed increment.
        var counter = rows.FindById(1);
        context.Check(counter["Counter"] == expectedCounter, "Final counter disagreed with acknowledged commits.");
        ledger.Acknowledge("rows", 1, counter);
        context.Metrics["concurrentCommittedUpdates"] = overlaps;
        context.Metrics["uniqueContentionWinners"] = uniqueWinners;
    }

    private static async Task<bool> Completes(Task task, TimeSpan timeout)
    {
        var winner = await Task.WhenAny(task, Task.Delay(timeout));
        if (winner != task) return false;
        await task;
        return true;
    }

    private static BsonDocument Clone(BsonDocument document) =>
        BsonSerializer.Deserialize(BsonSerializer.Serialize(document));
}
