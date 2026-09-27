using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using LiteDB;
using JsonSerializer = System.Text.Json.JsonSerializer;

internal static class Measurements
{
    private static int _sink;

    internal static void Run(string[] args)
    {
        if (args[0] == "measure-numbers")
        {
            const int iterations = 200000;
            var integer = new BsonValue(123);
            var fraction = new BsonValue(0.1d);
            var decimalFraction = new BsonValue(0.1m);
            Measure("integer-hash", iterations, () => integer.GetHashCode());
            Measure("mixed-number-compare", iterations, () => fraction.CompareTo(decimalFraction));
            return;
        }
        var count = int.Parse(args[2]);
        var settings = new ConnectionString
        {
            Filename = args[1], Password = args[3] == "encrypted" ? "measurement" : null,
            Collation = Collation.Binary
        };
        if (args[0] == "measure-create")
        {
            using var db = new LiteDatabase(settings);
            var rows = db.GetCollection("rows");
            for (var start = 0; start < count; start += 1000)
                rows.Insert(Enumerable.Range(start, Math.Min(1000, count - start)).Select(i => new BsonDocument
                {
                    ["_id"] = i + 1, ["key"] = new string('A', 200) + i,
                    ["values"] = new BsonArray { i, -i }
                }));
            rows.EnsureIndex("key", true);
            rows.EnsureIndex("computed", "LOWER($.key)", true);
            rows.EnsureIndex("values", "$.values[*]");
            db.Checkpoint();
            return;
        }
        var sourceBytes = new FileInfo(settings.Filename).Length;
        long sampledPeakDiskBytes = sourceBytes;
        using var sampler = new Timer(_ =>
        {
            try
            {
                var bytes = Directory.EnumerateFiles(Path.GetDirectoryName(settings.Filename),
                    Path.GetFileNameWithoutExtension(settings.Filename) + "*")
                    .Sum(path => new FileInfo(path).Length);
                if (bytes > Interlocked.Read(ref sampledPeakDiskBytes))
                    Interlocked.Exchange(ref sampledPeakDiskBytes, bytes);
            }
            catch (IOException) { } // A checkpoint can remove WAL/temp between samples.
        }, null, 0, 10);
        var allocated = GC.GetTotalAllocatedBytes(true);
        var timer = Stopwatch.StartNew();
        using var migrated = new LiteDatabase(settings);
        if (migrated.GetCollection("rows").Count() != count) throw new Exception("Lost records");
        timer.Stop();
        var openMilliseconds = timer.Elapsed.TotalMilliseconds;
        var migrationAllocations = GC.GetTotalAllocatedBytes(true) - allocated;
        var logFile = Path.ChangeExtension(settings.Filename, null) + "-log.db";
        var walBytes = File.Exists(logFile) ? new FileInfo(logFile).Length : 0;
        timer.Restart();
        migrated.Checkpoint();
        timer.Stop();
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            operation = "migration", encrypted = settings.Password != null, count, sourceBytes,
            migratedBytes = new FileInfo(settings.Filename).Length, openMilliseconds,
            checkpointMilliseconds = timer.Elapsed.TotalMilliseconds, migrationAllocations, walBytes,
            sampledPeakDiskBytes = Interlocked.Read(ref sampledPeakDiskBytes),
            peakWorkingSet = Process.GetCurrentProcess().PeakWorkingSet64
        }));
    }

    private static void Measure(string operation, int iterations, Func<int> action)
    {
        for (var i = 0; i < 10000; i++) _sink ^= action();
        GC.Collect();
        var allocated = GC.GetAllocatedBytesForCurrentThread();
        var timer = Stopwatch.StartNew();
        for (var i = 0; i < iterations; i++) _sink ^= action();
        timer.Stop();
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            operation, iterations, milliseconds = timer.Elapsed.TotalMilliseconds,
            allocatedBytes = GC.GetAllocatedBytesForCurrentThread() - allocated, sink = _sink
        }));
    }
}
