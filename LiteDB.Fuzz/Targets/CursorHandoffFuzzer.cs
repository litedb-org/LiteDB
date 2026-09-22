using LiteDB.Engine;

namespace LiteDB.Fuzz.Targets;

internal sealed class CursorHandoffFuzzer : IFuzzTarget
{
    public string Name => "cursor-handoff";
    public string Description => "Retired-thread cursors, foreign drain/disposal, snapshot writes, and checkpoint/rebuild.";

    public Task RunAsync(FuzzContext context)
    {
        var file = context.RegisterFile(Path.Combine(context.DirectoryPath, "handoff.db"));
        using (var engine = new LiteEngine(new EngineSettings { Filename = file, TransactionPageLimit = 3 }))
        using (var db = new LiteDatabase(engine, disposeOnClose: false))
        {
            db.Timeout = TimeSpan.FromSeconds(5);
            db.CheckpointSize = 0;
            var rows = db.GetCollection("rows");
            rows.Insert(Enumerable.Range(1, 12).Select(id => new BsonDocument { ["_id"] = id, ["version"] = 0 }));
            var handedOff = 0;
            while (context.Next())
            {
                var cursors = new List<IEnumerator<BsonDocument>>();
                var count = context.Random.Next(1, 17);
                var version = context.Steps - 1;
                var rebuild = context.Random.Next(4) == 0;
                context.Trace("pin-retired-readers", new { count, version, rebuild });
                try
                {
                    for (var i = 0; i < count; i++) FuzzThread.Run(() =>
                    {
                        var cursor = rows.Query().OrderBy("_id").ToEnumerable().GetEnumerator();
                        cursors.Add(cursor);
                        context.Check(cursor.MoveNext(), "Handoff cursor was empty.");
                    });
                    FuzzThread.CollectRetiredThreads();
                    // Churn enough short-lived threads to exercise recycled IDs on runtimes
                    // with an ID free list; no managed thread IDs enter the replay trace.
                    for (var i = 0; i < 128; i++) FuzzThread.Run(() =>
                        context.Check(engine.GetMonitor().GetThreadTransaction() == null,
                            "A new thread inherited a retired cursor's transaction."));

                    FuzzThread.Run(() => rows.UpdateMany(BsonExpression.Create("{ version: @0 }", context.Steps), "true"));
                    while (cursors.Count > 0)
                    {
                        var index = context.Random.Next(cursors.Count);
                        var drain = context.Random.Next(2) == 0;
                        context.Trace("handoff", new { index, drain });
                        var cursor = cursors[index];
                        var last = cursors.Count == 1;
                        using var checkpointStarted = new ManualResetEventSlim();
                        Task checkpoint = null;
                        if (last)
                        {
                            engine.SimulateBeforeExclusiveAdmission = checkpointStarted.Set;
                            engine.SimulateAfterExclusiveAdmission = () => context.Check(
                                engine.GetMonitor().Transactions.Count == 0, "Checkpoint passed a live cursor.");
                            checkpoint = Task.Factory.StartNew(db.Checkpoint, CancellationToken.None,
                                TaskCreationOptions.LongRunning, TaskScheduler.Default);
                            context.Check(checkpointStarted.Wait(TimeSpan.FromSeconds(5)), "Checkpoint did not start.");
                        }
                        FuzzThread.Run(() =>
                        {
                            // Disposing an old query must not release this independent transaction.
                            if (!last) context.Check(db.BeginTrans(), "Foreign explicit transaction was unavailable.");
                            try
                            {
                                if (drain)
                                {
                                    var seen = 0;
                                    do
                                    {
                                        context.Check(cursor.Current["_id"] == ++seen && cursor.Current["version"] == version,
                                            "Transferred cursor lost its original snapshot.");
                                    } while (cursor.MoveNext());
                                    context.Check(seen == 12, "Transferred cursor lost rows.");
                                }
                                cursor.Dispose();
                                cursor.Dispose();
                                if (!last)
                                {
                                    rows.UpdateMany("{ version: -1 }", "true");
                                    context.Check(db.Rollback(), "Foreign disposal lost the caller's transaction.");
                                }
                            }
                            finally { if (!last) db.Rollback(); }
                        });
                        if (checkpoint != null)
                        {
                            context.Check(checkpoint.Wait(TimeSpan.FromSeconds(10)), "Checkpoint did not finish after handoff.");
                            checkpoint.GetAwaiter().GetResult();
                            engine.SimulateBeforeExclusiveAdmission = null;
                            engine.SimulateAfterExclusiveAdmission = null;
                        }
                        cursors.RemoveAt(index);
                        context.Check(engine.GetMonitor().Transactions.Count == cursors.Count,
                            "Cursor release leaked or removed another transaction.");
                        handedOff++;
                    }
                    FuzzThread.Run(db.Checkpoint);
                    if (rebuild) db.Rebuild();
                    context.Check(rows.Count("version = @0", context.Steps) == 12,
                        "Handoff or checkpoint changed committed data.");
                    context.ObserveNovelty("cursor-handoff", count, rebuild);
                }
                finally { foreach (var cursor in cursors) cursor.Dispose(); }
            }
            db.Checkpoint();
            context.Metrics["cursorsHandedOff"] = handedOff;
        }
        DatabaseIntegrityVerifier.Verify(context, file);
        return Task.CompletedTask;
    }
}
