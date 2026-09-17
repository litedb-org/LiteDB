using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using LiteDB;
using LiteDB.ReproRunner.Shared;
using LiteDB.ReproRunner.Shared.Messaging;

internal static class Program
{
    private static int Main()
    {
        ReproConfigurationReporter.SendConfiguration(ReproHostClient.CreateDefault());
        var directory = Path.Combine(Path.GetTempPath(), "litedb-2846-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var count = int.Parse(Environment.GetEnvironmentVariable("LITEDB_REPRO_ROWS") ?? "100000");
            var samples = new double[3];
            var completedSamples = new double[3];
            for (var repetition = 0; repetition < samples.Length; repetition++)
            {
                var path = Path.Combine(directory, repetition + ".db");
                using (var db = new LiteDatabase(path))
                {
                    db.CheckpointSize = 0;
                    db.GetCollection("sentinel").Insert(new BsonDocument { ["_id"] = 7, ["value"] = "must survive" });
                    var col = db.GetCollection("drop");
                    col.InsertBulk(Enumerable.Range(1, count).Select(id => new BsonDocument
                    {
                        ["_id"] = id, ["group"] = id % 101, ["reverse"] = count - id,
                        ["payload"] = new string((char)('A' + id % 26), 2000)
                    }));
                    col.EnsureIndex("group");
                    col.EnsureIndex("reverse");
                    if (col.Count() != count || col.FindById(count)["payload"].AsString.Length != 2000)
                        throw new Exception("setup is incomplete");
                    db.Checkpoint();
                    var completion = CreateCompletionBarrier(db);
                    var timer = Stopwatch.StartNew();
                    if (!db.DropCollection("drop")) throw new Exception("drop reported false");
                    samples[repetition] = timer.Elapsed.TotalMilliseconds;
                    completion();
                    timer.Stop();
                    completedSamples[repetition] = timer.Elapsed.TotalMilliseconds;
                    Console.WriteLine($"SAMPLE rows={count}, returnMilliseconds={samples[repetition]:F3}, completedMilliseconds={completedSamples[repetition]:F3}, dataBytes={new FileInfo(path).Length}");
                    // Copy before Dispose can checkpoint from memory and conceal an
                    // incomplete WAL. The completed drop must already be recoverable.
                    var snapshot = Path.Combine(directory, repetition + "-completed.db");
                    File.Copy(path, snapshot);
                    File.Copy(Path.ChangeExtension(path, null) + "-log.db", Path.ChangeExtension(snapshot, null) + "-log.db");
                    using var completed = new LiteDatabase(snapshot);
                    VerifyDropped(completed);
                }
                using (var reopened = new LiteDatabase(path))
                {
                    VerifyDropped(reopened);
                    reopened.GetCollection("drop").Insert(new BsonDocument { ["_id"] = count, ["value"] = "new generation" });
                }
                using (var again = new LiteDatabase(path))
                {
                    var rows = again.GetCollection("drop").FindAll().ToArray();
                    if (rows.Length != 1 || rows[0]["_id"].AsInt32 != count || rows[0]["value"].AsString != "new generation")
                        throw new Exception("reusing the collection did not persist independently");
                }
            }
            Array.Sort(samples);
            Array.Sort(completedSamples);
            Console.WriteLine("MEASURED_2846 " + System.Text.Json.JsonSerializer.Serialize(new
            {
                rows = count, medianMilliseconds = samples[1],
                medianCompletedMilliseconds = completedSamples[1]
            }));
            return 10;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 20; }
        finally { Directory.Delete(directory, true); }
    }

    private static void VerifyDropped(LiteDatabase database)
    {
        if (database.CollectionExists("drop") || database.GetCollection("drop").Count() != 0)
            throw new Exception("dropped rows returned after reopening");
        if (database.GetCollection("sentinel").FindById(7)?["value"].AsString != "must survive")
            throw new Exception("drop damaged the independent collection");
    }

    private static Action CreateCompletionBarrier(LiteDatabase database)
    {
        var source = typeof(Program).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .Single(x => x.Key == "LiteDB.ReproRunner.UseProjectReference").Value == "true";
        var flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var engine = typeof(LiteDatabase).GetField("_engine", flags)?.GetValue(database)
            ?? throw new Exception("Cannot inspect the measurement engine.");
        var disk = engine.GetType().GetField("_disk", flags)?.GetValue(engine)
            ?? throw new Exception("Cannot inspect the measurement disk.");
        var queueProperty = disk.GetType().GetProperty("Queue");
        if (source)
        {
            if (queueProperty != null) throw new Exception("Source now has a background writer; update the completion barrier.");
            return () => { }; // Current Commit durably flushes the WAL before returning.
        }
        // 5.0.9 acknowledges the commit before its background writer finishes.
        // Wait joins the writer task, including its FileStream.Flush(true).
        if (typeof(LiteDatabase).Assembly.GetName().Version != new Version(5, 0, 9, 0))
            throw new Exception("The legacy completion barrier is specific to package 5.0.9.");
        var queue = queueProperty?.GetValue(disk) ?? throw new Exception("Legacy WAL writer queue is missing.");
        var wait = queue.GetType().GetMethod("Wait") ?? throw new Exception("Legacy queue Wait is missing.");
        var waitForWriter = (Action)Delegate.CreateDelegate(typeof(Action), queue, wait);
        var stream = queue.GetType().GetField("_stream", flags)?.GetValue(queue) as FileStream
            ?? throw new Exception("Legacy WAL writer is not a FileStream.");
        return () =>
        {
            waitForWriter();
            // 5.0.9 swallows writer IOExceptions. Require an observable successful
            // durable flush; a copied file alone cannot prove OS-cache durability.
            stream.Flush(true);
        };
    }

}
