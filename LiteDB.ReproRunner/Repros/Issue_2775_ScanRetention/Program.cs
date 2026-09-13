using System;
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
        var directory = Path.Combine(Path.GetTempPath(), "litedb-2775-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var count = int.Parse(Environment.GetEnvironmentVariable("LITEDB_REPRO_ROWS") ?? "360000");
            if (count < 100000) throw new ArgumentOutOfRangeException(nameof(count));
            var path = Path.Combine(directory, "data.db");
            using (var setup = new LiteDatabase(path))
            {
                setup.GetCollection("rows").InsertBulk(Enumerable.Range(1, count)
                    .Select(id => new BsonDocument { ["_id"] = id, ["value"] = id * 7L }));
            }
            var connection = new ConnectionString(path);
            // Both variants use the same public settings when available. Older packages
            // lack these controls; the emitted metadata makes that difference explicit.
            var cache = typeof(ConnectionString).GetProperty("CacheSize");
            var pages = typeof(ConnectionString).GetProperty("TransactionPageLimit");
            cache?.SetValue(connection, 8L * 1024 * 1024);
            pages?.SetValue(connection, 100);
            Console.WriteLine($"SETTINGS boundedCache={cache != null}, boundedTransaction={pages != null}");
            using var db = new LiteDatabase(connection);
            using var cursor = db.GetCollection("rows").FindAll().GetEnumerator();
            long firstHeap = 0;
            long sum = 0;
            for (var expected = 1; expected <= count; expected++)
            {
                if (!cursor.MoveNext()) throw new Exception("scan ended early");
                if (cursor.Current["_id"].AsInt32 != expected || cursor.Current["value"].AsInt64 != expected * 7L)
                    throw new Exception("scan returned wrong identity or payload");
                sum += cursor.Current["value"].AsInt64;
                if (expected == count / 3) firstHeap = GC.GetTotalMemory(true);
            }
            // Sample while the iterator is still alive: disposal would hide the retention.
            var growth = GC.GetTotalMemory(true) - firstHeap;
            GC.KeepAlive(cursor);
            if (cursor.MoveNext() || sum != 7L * count * (count + 1L) / 2) throw new Exception("scan multiplicity/checksum mismatch");
            Console.WriteLine($"MEASUREMENT rows={count}, additionalRetainedBytes={growth}");
            if (growth > 4L * 1024 * 1024)
            {
                Console.WriteLine("BUG_2775_CONFIRMED: a scalar-key scan retained more than 4 MiB of additional state after its first third");
                return 0;
            }
            Console.WriteLine("VERIFIED_2775: scan contents and bounded retention passed at the measured size");
            return 10;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 20; }
        finally { Directory.Delete(directory, true); }
    }
}
