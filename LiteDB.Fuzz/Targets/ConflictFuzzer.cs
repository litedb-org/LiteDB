using System.Collections.Concurrent;

namespace LiteDB.Fuzz.Targets;

internal sealed class ConflictFuzzer : IFuzzTarget
{
    public string Name => "conflict";
    public string Description => "Barrier-forced writer/schema/drop/storage/rebuild conflicts with acknowledged-state oracles.";

    public async Task RunAsync(FuzzContext context)
    {
        var conflicts = 0;
        while (context.Next())
        {
            var mode = Environment.GetEnvironmentVariable("LITEDB_CONFLICT_MODE") ?? "all";
            var file = context.StepFile($"conflict-{context.Steps}.db");
            var connection = new ConnectionString { Filename = file, TransactionPageLimit = 2, DurableCommits = true };
            var db = new LiteDatabase(connection);
            try
            {
                var rows = db.GetCollection("rows");
                rows.InsertBulk(Enumerable.Range(1, 12).Select(id => Document(id, id)));
                rows.EnsureIndex("value", "value");
                if (Enabled(mode, "schema")) await SchemaAndStorageConflict(context, db);
                if (Enabled(mode, "drop")) await DropAndWriterConflict(context, db);
                var acknowledged = Enabled(mode, "rebuild") && await RebuildAndWriterConflict(context, db);
                try { db.Dispose(); } catch (Exception error) when (PublicFailure(error)) { }
                db = new LiteDatabase(connection);
                rows = db.GetCollection("rows");
                if (acknowledged)
                    context.Check(rows.FindById(300)?["value"] == context.Steps,
                        "Concurrent rebuild lost an acknowledged writer.");
                db.Checkpoint();
                DatabaseIntegrityVerifier.Verify(context, file);
                conflicts += 6;
                context.ObserveNovelty("conflicting-operations", acknowledged, rows.Count() / 8);
            }
            finally { db.Dispose(); }
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }
        context.Metrics["forcedConflictingOperations"] = conflicts;
    }

    private static async Task SchemaAndStorageConflict(FuzzContext context, LiteDatabase db)
    {
        var payloads = new[]
        {
            Enumerable.Repeat((byte)0x5a, 300_000).ToArray(),
            Enumerable.Repeat((byte)0xa5, 300_000).ToArray()
        };
        using var barrier = new Barrier(4);
        var errors = new ConcurrentQueue<Exception>();
        var tasks = new[]
        {
            Run(() => db.GetCollection("rows").UpdateMany("{ value: value + 1 }", "_id <= 6")),
            Run(() => { db.GetCollection("rows").DropIndex("value"); db.GetCollection("rows").EnsureIndex("value", "value"); }),
            Run(() => { using var source = new MemoryStream(payloads[0]); db.FileStorage.Upload("same", "a.bin", source); }),
            Run(() => { using var source = new MemoryStream(payloads[1]); db.FileStorage.Upload("same", "b.bin", source); })
        };
        await Task.WhenAll(tasks);
        context.Check(errors.IsEmpty, "Schema/storage conflict leaked an internal failure: " +
            string.Join(" | ", errors.Select(error => error.ToString())));
        using var output = new MemoryStream();
        db.FileStorage.Download("same", output);
        context.Check(payloads.Any(payload => payload.SequenceEqual(output.ToArray())),
            "Same-ID concurrent uploads exposed mixed chunks.");

        Task Run(Action action) => Task.Run(() =>
        {
            barrier.SignalAndWait();
            try { action(); }
            catch (Exception error) when (PublicFailure(error)) { }
            catch (Exception error) { errors.Enqueue(error); }
        });
    }

    private static async Task DropAndWriterConflict(FuzzContext context, LiteDatabase db)
    {
        var scratch = db.GetCollection("scratch");
        scratch.Upsert(Document(1, 1));
        using var barrier = new Barrier(2);
        var writer = Task.Run(() =>
        {
            barrier.SignalAndWait();
            try { db.GetCollection("scratch").Upsert(Document(2, context.Steps)); }
            catch (Exception error) when (PublicFailure(error)) { }
        });
        var drop = Task.Run(() =>
        {
            barrier.SignalAndWait();
            try { db.DropCollection("scratch"); }
            catch (Exception error) when (PublicFailure(error)) { }
        });
        await Task.WhenAll(writer, drop);
        var actual = db.GetCollection("scratch").FindAll().ToArray();
        context.Check(actual.Length == 0 || actual.Length == 1 && actual[0]["_id"] == 2,
            "Drop-vs-writer produced a partial or impossible collection state.");
    }

    private static async Task<bool> RebuildAndWriterConflict(FuzzContext context, LiteDatabase db)
    {
        using var barrier = new Barrier(2);
        var acknowledged = false;
        var writer = Task.Run(() =>
        {
            barrier.SignalAndWait();
            try
            {
                db.GetCollection("rows").Upsert(Document(300, context.Steps));
                acknowledged = true;
            }
            catch (Exception error) when (PublicFailure(error)) { }
        });
        var rebuild = Task.Run(() =>
        {
            barrier.SignalAndWait();
            try { db.Rebuild(); }
            catch (Exception error) when (PublicFailure(error)) { }
        });
        await Task.WhenAll(writer, rebuild);
        return acknowledged;
    }

    private static bool PublicFailure(Exception error) => error is LiteException or IOException or
        InvalidOperationException or NotSupportedException or TimeoutException;

    private static bool Enabled(string mode, string operation) => mode == "all" ||
        mode.Split('-', StringSplitOptions.RemoveEmptyEntries).Contains(operation, StringComparer.Ordinal);

    private static BsonDocument Document(int id, int value) => new()
    {
        ["_id"] = id, ["value"] = value, ["payload"] = new byte[2000 + id % 17]
    };
}
