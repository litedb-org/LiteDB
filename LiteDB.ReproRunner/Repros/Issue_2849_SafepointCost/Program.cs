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
        var directory = Path.Combine(Path.GetTempPath(), "litedb-2849-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var count = int.Parse(Environment.GetEnvironmentVariable("LITEDB_REPRO_ROWS") ?? "1000000");
            if (count < 2) throw new ArgumentOutOfRangeException("LITEDB_REPRO_ROWS");
            // Random insertion and IN-list deletion revisit pages beyond the soft cache
            // budget. Sequential insertion plus a full index scan hid the reported cost.
            var random = new Random(2849);
            var ids = Enumerable.Range(1, count).OrderBy(_ => random.Next()).ToArray();
            var deleteIds = ids.Where(id => id % 2 == 0).Select(id => new BsonValue(id)).ToArray();
            var defaultInsert = new double[3];
            var defaultDelete = new double[3];
            var largeInsert = new double[3];
            var largeDelete = new double[3];
            var pageProperty = typeof(ConnectionString).GetProperty("TransactionPageLimit");
            var sourceBuild = bool.Parse(typeof(Program).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
                .Single(attribute => attribute.Key == "LiteDB.ReproRunner.UseProjectReference").Value!);
            if (sourceBuild && pageProperty == null) throw new Exception("current-source page-budget control is missing");
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
                        var inserted = col.Insert(ids.Select(id => new BsonDocument
                        {
                            ["_id"] = id, ["payload"] = new string((char)('A' + id % 26), 2000)
                        }));
                        timer.Stop();
                        (large ? largeInsert : defaultInsert)[round] = timer.Elapsed.TotalMilliseconds;
                        if (inserted != count || col.Count() != count) throw new Exception("insert count mismatch");
                        VerifyRows(col, count, false);
                        db.Checkpoint();
                        timer.Restart();
                        // Keep the shuffled IN-list workload as data. Compiling a
                        // half-million-element literal exhausts the Windows stack
                        // before the page-budget comparison can finish.
                        var deleted = col.DeleteMany(BsonExpression.Create("_id IN @0", new BsonArray(deleteIds)));
                        timer.Stop();
                        (large ? largeDelete : defaultDelete)[round] = timer.Elapsed.TotalMilliseconds;
                        if (deleted != count / 2) throw new Exception("delete count mismatch");
                    }
                    using (var reopened = new LiteDatabase(path))
                    {
                        VerifyRows(reopened.GetCollection("rows"), count, true);
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

    private static void VerifyRows(ILiteCollection<BsonDocument> rows, int count, bool survivorsOnly)
    {
        var expected = 1;
        foreach (var row in rows.FindAll())
        {
            if (row["_id"].AsInt32 != expected || row["payload"].AsString != new string((char)('A' + expected % 26), 2000))
                throw new Exception("wrong stored identity or payload");
            expected += survivorsOnly ? 2 : 1;
        }
        var end = survivorsOnly && count % 2 != 0 ? count + 2 : count + 1;
        if (expected != end) throw new Exception("missing stored rows");
    }
}
