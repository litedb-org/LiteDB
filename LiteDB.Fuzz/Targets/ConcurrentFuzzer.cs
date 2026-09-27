using System.Collections.Concurrent;

namespace LiteDB.Fuzz.Targets;

internal sealed class ConcurrentFuzzer : IFuzzTarget
{
    public string Name => "concurrent";
    public string Description => "Same-LiteDatabase thread contention, transactions, unique keys, cursors, and checkpoints.";

    public async Task RunAsync(FuzzContext context)
    {
        var file = context.RegisterFile(Path.Combine(context.DirectoryPath, "concurrent.db"));
        using var db = new LiteDatabase(new ConnectionString
        {
            Filename = file, TransactionPageLimit = 3
        });
        db.Timeout = TimeSpan.FromSeconds(10);
        db.CheckpointSize = 0;
        var rows = db.GetCollection("rows");
        rows.EnsureIndex("unique", "Unique", true);
        rows.Insert(new BsonDocument { ["_id"] = 1, ["Counter"] = 0, ["Unique"] = "control" });
        var expectedCounter = 0;
        var uniqueWinners = 0;
        var overlaps = 0;

        while (context.Next())
        {
            var snapshotCounter = rows.FindById(1)["Counter"].AsInt32;
            using var cursor = rows.Query().OrderBy("_id").ToEnumerable().GetEnumerator();
            context.Check(cursor.MoveNext(), "Concurrent reader could not pin its snapshot.");
            using var start = new Barrier(5);
            var committed = new ConcurrentBag<int>();
            var failures = new ConcurrentQueue<Exception>();
            var tasks = Enumerable.Range(0, 4).Select(worker => Task.Factory.StartNew(() =>
            {
                start.SignalAndWait();
                try
                {
                    context.Check(db.BeginTrans(), "Concurrent worker could not begin a transaction.");
                    var changed = db.GetCollection("rows").UpdateMany("{ Counter: Counter + 1 }", "_id = 1");
                    context.Check(changed == 1, "Concurrent counter update changed the wrong row count.");
                    if (!db.Commit()) throw new InvalidOperationException("Concurrent commit returned false.");
                    committed.Add(worker);
                }
                catch (Exception error)
                {
                    try { db.Rollback(); } catch { }
                    failures.Enqueue(error);
                }
            }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default)).ToArray();
            start.SignalAndWait();
            await Task.WhenAll(tasks);
            context.Check(failures.IsEmpty, "Concurrent transaction failed: " +
                string.Join(" | ", failures.Select(error => error.Message)));
            expectedCounter += committed.Count;
            context.Check(committed.Count == 4, "Not every same-document transaction committed.");

            var key = "round-" + context.Steps;
            var contenders = new ConcurrentBag<int>();
            var conflictTasks = Enumerable.Range(0, 4).Select(worker => Task.Run(() =>
            {
                var id = 10_000 + context.Steps * 10 + worker;
                try
                {
                    db.GetCollection("rows").Insert(new BsonDocument
                    {
                        ["_id"] = id, ["Unique"] = key, ["Counter"] = worker
                    });
                    contenders.Add(id);
                }
                catch (LiteException) { }
            })).ToArray();
            await Task.WhenAll(conflictTasks);
            context.Check(contenders.Count == 1, "Unique-key contention did not have exactly one winner.");
            uniqueWinners++;

            Task checkpoint;
            using (ExecutionContext.SuppressFlow())
                checkpoint = Task.Factory.StartNew(() => db.Checkpoint(), CancellationToken.None,
                    TaskCreationOptions.LongRunning, TaskScheduler.Default);
            var observed = new List<BsonDocument> { Clone(cursor.Current) };
            while (cursor.MoveNext()) observed.Add(Clone(cursor.Current));
            context.Check(observed.Single(document => document["_id"] == 1)["Counter"] == snapshotCounter,
                "Concurrent long cursor observed a later counter version.");
            context.Check(await Completes(checkpoint, TimeSpan.FromSeconds(15)),
                "Checkpoint did not finish after the concurrent cursor drained.");
            context.Check(rows.FindById(1)["Counter"] == expectedCounter,
                "Final counter disagreed with acknowledged concurrent commits.");
            context.Check(rows.Count(BsonExpression.Create("Unique = @0", key)) == 1,
                "Unique contention left zero or multiple indexed rows.");
            overlaps += committed.Count;

            if (context.Steps % 5 == 0)
            {
                db.Rebuild();
                rows = db.GetCollection("rows");
                context.Check(rows.FindById(1)["Counter"] == expectedCounter,
                    "Rebuild after contention changed the acknowledged counter.");
            }
            context.ObserveNovelty("same-process-contention", committed.Count, contenders.Count,
                context.Steps % 5 == 0);
        }
        db.Checkpoint();
        DatabaseIntegrityVerifier.Verify(context, file);
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
