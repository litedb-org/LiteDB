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
        var directory = Path.Combine(Path.GetTempPath(), "litedb-2849-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var count = int.Parse(Environment.GetEnvironmentVariable("LITEDB_REPRO_ROWS") ?? "100000");
            var defaultInsert = new double[3];
            var defaultDelete = new double[3];
            var largeInsert = new double[3];
            var largeDelete = new double[3];
            var pageProperty = typeof(ConnectionString).GetProperty("TransactionPageLimit");
            for (var round = 0; round < 3; round++)
            {
                // Alternate order to reduce systematic warm-cache and process-start bias.
                foreach (var large in round % 2 == 0 ? new[] { false, true } : new[] { true, false })
                {
                    var path = Path.Combine(directory, round + "-" + large + ".db");
                    var connection = new ConnectionString(path);
                    if (large) pageProperty?.SetValue(connection, 100000);
                    using (var db = new LiteDatabase(connection))
                    {
                        db.CheckpointSize = 0;
                        var col = db.GetCollection("rows");
                        var timer = Stopwatch.StartNew();
                        var inserted = col.Insert(Enumerable.Range(1, count).Select(id => new BsonDocument
                        {
                            ["_id"] = id, ["payload"] = new string((char)('A' + id % 26), 2000)
                        }));
                        timer.Stop();
                        (large ? largeInsert : defaultInsert)[round] = timer.Elapsed.TotalMilliseconds;
                        if (inserted != count || col.Count() != count) throw new Exception("insert count mismatch");
                        db.Checkpoint();
                        timer.Restart();
                        var deleted = col.DeleteMany("_id % 2 = 0");
                        timer.Stop();
                        (large ? largeDelete : defaultDelete)[round] = timer.Elapsed.TotalMilliseconds;
                        if (deleted != count / 2) throw new Exception("delete count mismatch");
                    }
                    using (var reopened = new LiteDatabase(path))
                    {
                        var expected = 1;
                        foreach (var row in reopened.GetCollection("rows").FindAll())
                        {
                            if (row["_id"].AsInt32 != expected || row["payload"].AsString != new string((char)('A' + expected % 26), 2000))
                                throw new Exception("wrong surviving identity or payload after reopening");
                            expected += 2;
                        }
                        if (expected != (count % 2 == 0 ? count + 1 : count + 2)) throw new Exception("missing survivors");
                    }
                    Console.WriteLine($"SAMPLE round={round}, largeBudget={large}, insertMs={(large ? largeInsert : defaultInsert)[round]:F3}, deleteMs={(large ? largeDelete : defaultDelete)[round]:F3}");
                }
            }
            Array.Sort(defaultInsert); Array.Sort(defaultDelete); Array.Sort(largeInsert); Array.Sort(largeDelete);
            var insertRatio = defaultInsert[1] / largeInsert[1];
            var deleteRatio = defaultDelete[1] / largeDelete[1];
            Console.WriteLine($"MEASUREMENT rows={count}, configurable={pageProperty != null}, insertRatio={insertRatio:F3}, deleteRatio={deleteRatio:F3}");
            if (pageProperty != null && (insertRatio > 2 || deleteRatio > 2))
            {
                Console.WriteLine("BUG_2849_CONFIRMED: default median write cost exceeds twice the large-budget control");
                return 0;
            }
            Console.WriteLine("VERIFIED_2849: persisted write effects passed; no twofold slowdown at this size");
            return 10;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 20; }
        finally { Directory.Delete(directory, true); }
    }
}
