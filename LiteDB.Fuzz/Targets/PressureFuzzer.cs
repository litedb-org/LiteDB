using LiteDB.Engine;

namespace LiteDB.Fuzz.Targets;

internal sealed class PressureFuzzer : IFuzzTarget
{
    public string Name => "pressure";
    public string Description => "Proven cache eviction with tiny auto-checkpoints, long readers, page churn, reopen, and cleanup.";

    public Task RunAsync(FuzzContext context)
    {
        var previous = LiteDB.Engine.EngineState.ObserveCacheEviction;
        var evictions = 0;
        LiteDB.Engine.EngineState.ObserveCacheEviction = _ => Interlocked.Increment(ref evictions);
        var file = context.RegisterFile(Path.Combine(context.DirectoryPath, "pressure.db"));
        try
        {
            using (var db = new LiteDatabase(new ConnectionString
            {
                Filename = file, CacheSize = 512 * 1024, TransactionPageLimit = 2,
                MemoryProfile = MemoryProfile.LowMemory, DurableCommits = true
            }))
            {
                db.CheckpointSize = 1;
                var rows = db.GetCollection("rows");
                rows.InsertBulk(Enumerable.Range(1, 3000).Select(id => new BsonDocument
                {
                    ["_id"] = id, ["value"] = id % 97, ["payload"] = new byte[1200 + id % 1400]
                }));
                rows.EnsureIndex("value", "value");
                db.Checkpoint();
                using var cursor = rows.Query().OrderBy("_id").ToEnumerable().GetEnumerator();
                context.Check(cursor.MoveNext(), "Pressure cursor was empty.");
                while (context.Next())
                {
                    var id = 4000 + context.Steps;
                    rows.Upsert(new BsonDocument { ["_id"] = id, ["value"] = id % 97, ["payload"] = new byte[5000] });
                    _ = rows.FindAll().ToArray();
                    cursor.MoveNext();
                    context.ObserveNovelty("resource-pressure", evictions / 10, context.Steps % 3);
                }
                while (cursor.MoveNext()) { }
                db.Checkpoint();
            }
            context.Check(evictions > 0, "Resource-pressure campaign did not prove a cache eviction.");
            using (var reopened = new LiteDatabase(file))
                context.Check(reopened.GetCollection("rows").Count() >= 3000, "Pressure reopen lost documents.");
            DatabaseIntegrityVerifier.Verify(context, file);
            context.Metrics["observedCacheEvictions"] = evictions;
        }
        finally { LiteDB.Engine.EngineState.ObserveCacheEviction = previous; }
        return Task.CompletedTask;
    }
}
