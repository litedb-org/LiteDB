using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
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
                    var timer = Stopwatch.StartNew();
                    if (!db.DropCollection("drop")) throw new Exception("drop reported false");
                    timer.Stop();
                    samples[repetition] = timer.Elapsed.TotalMilliseconds;
                    Console.WriteLine($"SAMPLE rows={count}, milliseconds={samples[repetition]:F3}, dataBytes={new FileInfo(path).Length}");
                }
                using (var reopened = new LiteDatabase(path))
                {
                    if (reopened.CollectionExists("drop") || reopened.GetCollection("drop").Count() != 0)
                        throw new Exception("dropped rows returned after reopening");
                    if (reopened.GetCollection("sentinel").FindById(7)["value"].AsString != "must survive")
                        throw new Exception("drop damaged the independent collection");
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
            Console.WriteLine("MEASURED_2846 " + System.Text.Json.JsonSerializer.Serialize(new { rows = count, medianMilliseconds = samples[1] }));
            return 10;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 20; }
        finally { Directory.Delete(directory, true); }
    }
}
