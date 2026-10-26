using LiteDB.Engine;
using LiteDB.Tests.Safety;
using LiteDB.Utils;

namespace LiteDB.Fuzz.Targets;

/// <remarks>
/// Oracles: each cursor open, write, handoff, checkpoint and rebuild runs under
/// <see cref="FuzzOracles.Deadline{T}"/> on the thread that issues it (rebuild declares its own);
/// the engine's dispose is followed by <see cref="FuzzOracles.ConnectionClean"/> and, after the
/// integrity walk, a cold reopen checks <see cref="FuzzOracles.Durable"/> for the last acknowledged
/// version of every row before <see cref="FuzzOracles.Quiescent"/> ends the scenario. The checkpoint
/// stage hook is a barrier, not an injected fault, so FaultReached/FaultDisposed do not apply;
/// Ownership does not apply to this Direct-mode engine.
/// </remarks>
internal sealed class CursorHandoffFuzzer : IFuzzTarget
{
    private const string Dimension = "mode=direct;handoff=retired-thread";
    private static readonly TimeSpan RebuildDeadline = TimeSpan.FromSeconds(60);

    public string Name => "cursor-handoff";
    public string Description => "Retired-thread cursors, foreign drain/disposal, snapshot writes, and checkpoint/rebuild.";

    public Task RunAsync(FuzzContext context)
    {
        var file = context.RegisterFile(Path.Combine(context.DirectoryPath, "handoff.db"));
        var engine = new LiteEngine(new EngineSettings { Filename = file, TransactionPageLimit = 3 });
        using (engine)
        using (var db = new LiteDatabase(engine, disposeOnClose: false))
        {
            db.Timeout = TimeSpan.FromSeconds(5);
            context.Oracles.PragmaTimeout = db.Timeout;
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
                    for (var i = 0; i < count; i++) FuzzThread.Run(() => context.Deadline("CursorOpen", () =>
                    {
                        var cursor = rows.Query().OrderBy("_id").ToEnumerable().GetEnumerator();
                        cursors.Add(cursor);
                        context.Check(cursor.MoveNext(), "Handoff cursor was empty.");
                    }, Dimension));
                    FuzzThread.CollectRetiredThreads();
                    // Churn enough short-lived threads to exercise recycled IDs on runtimes
                    // with an ID free list; no managed thread IDs enter the replay trace.
                    for (var i = 0; i < 128; i++) FuzzThread.Run(() =>
                        context.Check(engine.GetMonitor().GetThreadTransaction() == null,
                            "A new thread inherited a retired cursor's transaction."));

                    FuzzThread.Run(() => context.Deadline("UpdateMany",
                        () => rows.UpdateMany(BsonExpression.Create("{ version: @0 }", context.Steps), "true"), Dimension));
                    while (cursors.Count > 0)
                    {
                        var index = context.Random.Next(cursors.Count);
                        var drain = context.Random.Next(2) == 0;
                        context.Trace("handoff", new { index, drain });
                        var cursor = cursors[index];
                        var last = cursors.Count == 1;
                        using var checkpointStarted = new ManualResetEventSlim();
                        using var handoffStarted = new ManualResetEventSlim();
                        Task checkpoint = null;
                        if (last)
                        {
                            // MVCC checkpoint can proceed under a live cursor.
                            // Pause before index admission, then release both the
                            // checkpointer and foreign handoff from one barrier.
                            engine.CheckpointStage = stage =>
                            {
                                if (stage != "before-index-lock") return;
                                context.Check(engine.GetMonitor().Transactions.Count == 1,
                                    "Checkpoint did not overlap the final pinned cursor.");
                                checkpointStarted.Set();
                                context.Check(handoffStarted.Wait(TimeSpan.FromSeconds(5)), "Foreign handoff did not start.");
                            };
                            checkpoint = Task.Factory.StartNew(() => context.Deadline("Checkpoint", db.Checkpoint, Dimension),
                                CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
                            Reachability.Sometimes("situation:cursor-handoff-checkpoint-overlaps-last-cursor");
                            context.Check(checkpointStarted.Wait(TimeSpan.FromSeconds(5)), "Checkpoint did not start.");
                        }
                        FuzzThread.Run(() => context.Deadline("Handoff", () =>
                        {
                            handoffStarted.Set();
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
                            if (!last) Reachability.Sometimes("situation:cursor-handoff-foreign-disposal-keeps-transaction");
                        }, Dimension));
                        if (checkpoint != null)
                        {
                            context.Check(checkpoint.Wait(TimeSpan.FromSeconds(10)), "Checkpoint did not finish after handoff.");
                            checkpoint.GetAwaiter().GetResult();
                            engine.CheckpointStage = null;
                        }
                        cursors.RemoveAt(index);
                        context.Check(engine.GetMonitor().Transactions.Count == cursors.Count,
                            "Cursor release leaked or removed another transaction.");
                        handedOff++;
                    }
                    FuzzThread.Run(() => context.Deadline("Checkpoint", db.Checkpoint, Dimension));
                    if (rebuild) context.Deadline("Rebuild", () => db.Rebuild(), Dimension, RebuildDeadline);
                    context.Check(rows.Count("version = @0", context.Steps) == 12,
                        "Handoff or checkpoint changed committed data.");
                    context.ObserveNovelty("cursor-handoff", count, rebuild);
                }
                finally
                {
                    engine.CheckpointStage = null;
                    foreach (var cursor in cursors) cursor.Dispose();
                }
            }
            db.Checkpoint();
            context.Metrics["cursorsHandedOff"] = handedOff;
        }
        context.ConnectionClean(engine);
        DatabaseIntegrityVerifier.Verify(context, file);
        // After the structural walk, so its trace never sees the cold reopen.
        var ledger = new DurableLedger();
        ledger.ExpectExactly("rows", Enumerable.Range(1, 12)
            .Select(id => new BsonDocument { ["_id"] = id, ["version"] = context.Steps }));
        var cold = new LiteDatabase(file);
        using (cold) context.Durable(ledger, cold, "close and cold reopen");
        context.ConnectionClean(cold, "ColdReopenDispose");
        context.Quiescent(file);
        return Task.CompletedTask;
    }
}
