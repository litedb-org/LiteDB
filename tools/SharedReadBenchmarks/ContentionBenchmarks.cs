using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using LiteDB;

internal static class ContentionBenchmarks
{
    internal static void Run(string[] args)
    {
        var filename = Path.GetFullPath(args[1]);
        var worker = int.Parse(args[2]);
        var milliseconds = int.Parse(args[3]);
        var interval = int.Parse(args[4]);
        var signal = Path.GetFullPath(args[5]);
        using var process = Process.GetCurrentProcess();
        var cpu = process.TotalProcessorTime;
        var allocated = GC.GetTotalAllocatedBytes(true);
        var lifecycle = Stopwatch.StartNew();
        using var db = Open(filename);
        var own = db.GetCollection("writer" + worker);
        // A leased scan in the continuously writing owner's thread reproduces #3013's pin.
        using var held = worker == 0 ? db.GetCollection("rows").Query().OrderBy("_id").ToEnumerable().GetEnumerator() : null;
        if (held != null && !held.MoveNext()) throw new InvalidOperationException("Missing held snapshot");
        var openMs = lifecycle.Elapsed.TotalMilliseconds;
        File.WriteAllText(signal + ".ready-" + worker, "ready");
        var wait = Stopwatch.StartNew();
        while (!File.Exists(signal))
        {
            if (wait.Elapsed.TotalSeconds > 30) throw new TimeoutException("Start barrier");
            Thread.Sleep(1);
        }
        var startUtc = new DateTime(long.Parse(File.ReadAllText(signal)), DateTimeKind.Utc);
        while (DateTime.UtcNow < startUtc) Thread.Sleep(1);
        var clock = Stopwatch.StartNew();
        var api = new List<double>();
        var arrival = new List<double>();
        var wal = Path.Combine(Path.GetDirectoryName(filename), Path.GetFileNameWithoutExtension(filename) + "-log.db");
        long WalBytes()
        {
            try { return new FileInfo(wal).Length; }
            catch (FileNotFoundException) { return 0; }
        }
        var peakWal = WalBytes();
        var threads = process.Threads.Count;
        var handles = process.HandleCount;
        var next = 0.0;
        while (clock.Elapsed.TotalMilliseconds < milliseconds)
        {
            if (interval > 0 && clock.Elapsed.TotalMilliseconds < next)
            {
                Thread.Sleep(1);
                continue;
            }
            var before = clock.Elapsed.TotalMilliseconds;
            var revision = api.Count + 1;
            own.Upsert(new BsonDocument { ["_id"] = 1, ["revision"] = revision, ["payload"] = new string('w', 200) });
            var after = clock.Elapsed.TotalMilliseconds;
            api.Add(after - before);
            arrival.Add(after - (interval == 0 ? before : next));
            next += interval;
            peakWal = Math.Max(peakWal, WalBytes());
            if (revision % 32 == 0)
            {
                process.Refresh();
                threads = Math.Max(threads, process.Threads.Count);
                handles = Math.Max(handles, process.HandleCount);
            }
        }
        var activeMs = clock.Elapsed.TotalMilliseconds;
        if (held != null)
        {
            var id = 0;
            do
            {
                var row = held.Current;
                if (row["_id"].AsInt32 != id || row["revision"].AsInt32 != 0 ||
                    row["payload"].AsString != new string((char)('a' + id % 26), 4000) + ":0")
                    throw new InvalidOperationException("Changed held snapshot at " + id);
                id++;
            } while (held.MoveNext());
            if (id != 200) throw new InvalidOperationException("Missing held rows");
        }
        var close = Stopwatch.StartNew();
        held?.Dispose();
        db.Dispose();
        close.Stop();
        var walAfterClose = WalBytes();
        // Explicitly account for idle holder eviction and managed retention after close.
        Thread.Sleep(1200);
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        process.Refresh();
        var cpuMs = (process.TotalProcessorTime - cpu).TotalMilliseconds;
        var totalBytes = GC.GetTotalAllocatedBytes(true) - allocated;
        var apiSamples = api.OrderBy(x => x).ToArray();
        var arrivals = arrival.OrderBy(x => x).ToArray();
        if (apiSamples.Length == 0) throw new InvalidOperationException("Worker made no progress");
        double Percentile(double[] samples, double p) => samples[Math.Min(samples.Length - 1, (int)(p * samples.Length))];
        Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new
        {
            worker, interval, count = apiSamples.Length,
            offered = interval == 0 ? apiSamples.Length : milliseconds / interval,
            unfinished = interval == 0 ? 0 : Math.Max(0, milliseconds / interval - apiSamples.Length),
            activeMs, openMs, closeMs = close.Elapsed.TotalMilliseconds,
            lifecycleMs = lifecycle.Elapsed.TotalMilliseconds, cpuMs, totalAllocatedBytes = totalBytes,
            apiMeanMs = apiSamples.Average(), apiP50Ms = Percentile(apiSamples, .5), apiP95Ms = Percentile(apiSamples, .95),
            apiP99Ms = Percentile(apiSamples, .99), apiWorstMs = apiSamples.Last(),
            arrivalMeanMs = arrivals.Average(), arrivalP50Ms = Percentile(arrivals, .5), arrivalP95Ms = Percentile(arrivals, .95),
            arrivalP99Ms = Percentile(arrivals, .99), arrivalWorstMs = arrivals.Last(),
            sampledPeakWalBytes = peakWal, walAfterClose, sampledPeakThreads = threads, sampledPeakHandles = handles,
            peakWorkingSetBytes = process.PeakWorkingSet64, idleWorkingSetBytes = process.WorkingSet64,
            idleThreads = process.Threads.Count, idleHandles = process.HandleCount, retainedManagedBytes = GC.GetTotalMemory(false)
        }));
    }

    internal static void Verify(string filename, int[] counts)
    {
        using var db = Open(filename);
        for (var worker = 0; worker < counts.Length; worker++)
        {
            var row = db.GetCollection("writer" + worker).FindById(1);
            if (row == null || row.Count != 3 || row["revision"].AsInt32 != counts[worker] || row["payload"].AsString != new string('w', 200))
                throw new InvalidOperationException("Lost acknowledged state for worker " + worker);
        }
        var rows = db.GetCollection("rows").FindAll().OrderBy(row => row["_id"].AsInt32).ToArray();
        if (rows.Length != 200) throw new InvalidOperationException("Missing cold snapshot rows");
        for (var id = 0; id < rows.Length; id++)
            if (rows[id]["_id"].AsInt32 != id || rows[id]["revision"].AsInt32 != 0 ||
                rows[id]["payload"].AsString != new string((char)('a' + id % 26), 4000) + ":0")
                throw new InvalidOperationException("Changed cold snapshot row " + id);
        Console.WriteLine("verified");
    }

    private static LiteDatabase Open(string filename) => new LiteDatabase(new ConnectionString
    {
        Filename = filename, Connection = ConnectionType.Shared
    });
}
