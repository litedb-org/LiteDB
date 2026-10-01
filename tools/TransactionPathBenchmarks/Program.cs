using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using LiteDB;

// One workload per process. Uses only APIs that predate transaction handles, so the
// identical runner measures a baseline and a candidate library.
// Usage: TransactionPathBenchmarks <scratch-dir> <workload> [warmup-seconds] [windows]
if (args.Length < 2) throw new ArgumentException("Usage: <scratch-dir> <workload> [warmup-seconds] [windows]");
var scratch = Path.Combine(args[0], Guid.NewGuid().ToString("N"));
var workload = args[1];
var warmup = TimeSpan.FromSeconds(args.Length > 2 ? double.Parse(args[2], CultureInfo.InvariantCulture) : 3);
var windows = args.Length > 3 ? int.Parse(args[3], CultureInfo.InvariantCulture) : 5;
const int Rows = 1000;
Directory.CreateDirectory(scratch);
var file = Path.Combine(scratch, "bench.db");
var shared = workload.StartsWith("shared-", StringComparison.Ordinal);
var connection = new ConnectionString { Filename = file, Connection = shared ? ConnectionType.Shared : ConnectionType.Direct };
// A prerelease-labelled library refuses its first open until this process acknowledges it.
// Baseline and candidate are separate builds, so ask the loaded assembly, not this runner.
typeof(LiteDatabase).Assembly.GetType("LiteDB.LiteDBPragmas")
    ?.GetMethod("I_AM_AWARE_MY_DATABASE_BREAKS_WHEN_I_USE_THIS")?.Invoke(null, null);
try
{
    using (var seed = new LiteDatabase(connection))
    {
        var rows = seed.GetCollection("rows");
        for (var i = 1; i <= Rows; i++) rows.Insert(new BsonDocument { ["_id"] = i, ["value"] = i });
        seed.Checkpoint();
    }
    double[] rates;
    long operations = 0, allocated;
    using (var db = new LiteDatabase(connection))
    {
        var rows = db.GetCollection("rows");
        var next = 0;
        Action operation = workload switch
        {
            "direct-ordinary-read" or "shared-ordinary-read" => () =>
            {
                if (rows.FindById(1 + (next++ % Rows)) == null) throw new InvalidOperationException("Missing row");
            },
            "direct-legacy-read" or "shared-legacy-read" => () =>
            {
                db.BeginTrans();
                if (rows.FindById(1 + (next++ % Rows)) == null) throw new InvalidOperationException("Missing row");
                db.Commit();
            },
            "direct-ordinary-update" or "shared-ordinary-update" => () =>
            {
                var id = 1 + (next++ % Rows);
                if (!rows.Update(new BsonDocument { ["_id"] = id, ["value"] = id })) throw new InvalidOperationException("Missing row");
            },
            _ => throw new ArgumentException("Unknown workload " + workload)
        };
        var clock = Stopwatch.StartNew();
        while (clock.Elapsed < warmup) operation();
        rates = new double[windows];
        var before = GC.GetTotalAllocatedBytes(true);
        for (var w = 0; w < windows; w++)
        {
            var count = 0L;
            var window = Stopwatch.StartNew();
            while (window.Elapsed < TimeSpan.FromSeconds(1)) { operation(); count++; }
            rates[w] = count / window.Elapsed.TotalSeconds;
            operations += count;
        }
        allocated = GC.GetTotalAllocatedBytes(true) - before;
    }
    // A fast but wrong result is not a result: verify every row through a cold reopen.
    using (var cold = new LiteDatabase(connection))
    {
        var rows = cold.GetCollection("rows");
        if (rows.Count() != Rows) throw new InvalidOperationException("Row count changed");
        for (var i = 1; i <= Rows; i += 97)
            if (rows.FindById(i)?["value"].AsInt32 != i) throw new InvalidOperationException("Row value changed");
    }
    var sorted = (double[])rates.Clone();
    Array.Sort(sorted);
    Console.WriteLine("{\"workload\":\"" + workload + "\",\"opsPerSecond\":" +
        sorted[sorted.Length / 2].ToString("R", CultureInfo.InvariantCulture) +
        ",\"bytesPerOperation\":" + (allocated / (double)Math.Max(1, operations)).ToString("R", CultureInfo.InvariantCulture) +
        ",\"windows\":[" + string.Join(",", Array.ConvertAll(rates, r => r.ToString("R", CultureInfo.InvariantCulture))) + "]" +
        ",\"runtime\":\"" + Environment.Version + "\"}");
}
finally
{
    try { Directory.Delete(scratch, true); } catch { }
}
