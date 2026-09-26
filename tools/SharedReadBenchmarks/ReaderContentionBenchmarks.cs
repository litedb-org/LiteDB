using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Threading;
using LiteDB;

internal static class ReaderContentionBenchmarks
{
    private const int Rows = 200;
    private static string Payload(int id, int revision) => new string((char)('a' + id % 26), 4000) + ":" + revision;
    private static BsonDocument Row(int id, int revision) => new BsonDocument
    {
        ["_id"] = id, ["revision"] = revision, ["payload"] = Payload(id, revision)
    };
    private static LiteDatabase Open(string file) => new LiteDatabase(new ConnectionString
    {
        Filename = file, Connection = ConnectionType.Shared
    });

    internal static void Run(string[] args)
    {
        var filename = Path.GetFullPath(args[1]);
        if (args[0] == "read-contention-seed" && args.Length == 2)
        {
            using var db = Open(filename);
            db.GetCollection("rows").InsertBulk(Enumerable.Range(0, Rows).Select(id => Row(id, 0)));
            db.GetCollection("rows").EnsureIndex("revision");
            return;
        }
        if (args[0] == "read-contention-verify" && args.Length == 3)
        {
            using var db = Open(filename);
            Validate(db.GetCollection("rows").FindAll(), Rows, int.Parse(args[2]));
            Validate(db.GetCollection("rows").Query().OrderBy("revision").ToEnumerable(), Rows, int.Parse(args[2]));
            Console.WriteLine("verified");
            return;
        }
        if (args[0] != "read-contention" || args.Length != 8)
            throw new ArgumentException("read-contention <database> <worker> <point|medium|large|writer|checkpoint> <warmup-ms> <measure-ms> <signal> <readers>");
        var worker = int.Parse(args[2]);
        var role = args[3];
        if (!new[] { "point", "medium", "large", "writer", "checkpoint" }.Contains(role)) throw new ArgumentException("role");
        var warmupMs = int.Parse(args[4]);
        var measureMs = int.Parse(args[5]);
        var signal = Path.GetFullPath(args[6]);
        var readers = int.Parse(args[7]);
        using var process = Process.GetCurrentProcess();
        using var dbWorker = Open(filename);
        var rows = dbWorker.GetCollection("rows");
        // Precompile the exact operation before synchronized warmup.
        Validate(rows.FindAll(), Rows, null);
        File.WriteAllText(signal + ".ready-" + worker, "ready");
        var waiting = Stopwatch.StartNew();
        while (!File.Exists(signal))
        {
            if (waiting.Elapsed.TotalSeconds > 30) throw new TimeoutException("Reader benchmark barrier");
            Thread.Sleep(1);
        }
        var start = new DateTime(long.Parse(File.ReadAllText(signal)), DateTimeKind.Utc);
        while (DateTime.UtcNow < start) Thread.Sleep(1);
        var clock = Stopwatch.StartNew();
        var samples = new List<double>();
        var revision = 0;
        var minimum = 0;
        var warmupCount = 0;
        var checkpointCount = 0;
        var checkpointMs = 0.0;
        void Operation()
        {
            if (role == "writer" || role == "checkpoint")
            {
                var next = revision + 1;
                dbWorker.BeginTrans();
                for (var id = 0; id < Rows; id++)
                    if (!rows.Update(Row(id, next))) throw new InvalidOperationException("Missing update " + id);
                if (!dbWorker.Commit()) throw new InvalidOperationException("Missing commit");
                revision = next;
                if (role == "checkpoint")
                {
                    var before = Stopwatch.GetTimestamp();
                    dbWorker.Checkpoint();
                    checkpointMs += (Stopwatch.GetTimestamp() - before) * 1000.0 / Stopwatch.Frequency;
                    checkpointCount++;
                }
            }
            else
            {
                int observed;
                if (role == "point")
                {
                    var id = (warmupCount + samples.Count) % Rows;
                    var row = rows.FindById(id);
                    observed = Check(row, id, null);
                }
                else
                {
                    var query = rows.Query().OrderBy("revision");
                    observed = Validate(role == "medium" ? query.Limit(50).ToEnumerable() : query.ToEnumerable(),
                        role == "medium" ? 50 : Rows, null);
                }
                if (observed < minimum) throw new InvalidOperationException("Snapshot moved backwards");
                minimum = observed;
            }
        }
        while (clock.Elapsed.TotalMilliseconds < warmupMs) { Operation(); warmupCount++; }
        checkpointCount = 0;
        checkpointMs = 0;
        var cpu = process.TotalProcessorTime;
        var allocation = GC.GetTotalAllocatedBytes(true);
        var active = Stopwatch.StartNew();
        while (active.Elapsed.TotalMilliseconds < measureMs)
        {
            var before = Stopwatch.GetTimestamp();
            Operation();
            samples.Add((Stopwatch.GetTimestamp() - before) * 1000.0 / Stopwatch.Frequency);
        }
        var activeMs = active.Elapsed.TotalMilliseconds;
        var activeCpuMs = (process.TotalProcessorTime - cpu).TotalMilliseconds;
        var activeAllocated = GC.GetTotalAllocatedBytes(true) - allocation;
        var close = Stopwatch.StartNew();
        dbWorker.Dispose();
        close.Stop();
        Thread.Sleep(1200);
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        process.Refresh();
        var lifecycleCpuMs = (process.TotalProcessorTime - cpu).TotalMilliseconds;
        var lifecycleAllocated = GC.GetTotalAllocatedBytes(true) - allocation;
        var chronologicalWindows = Enumerable.Range(0, 5).Select(i => samples.Skip(i * samples.Count / 5)
            .Take((i + 1) * samples.Count / 5 - i * samples.Count / 5).DefaultIfEmpty().Average()).ToArray();
        samples.Sort();
        if (samples.Count == 0) throw new InvalidOperationException("Worker made no progress");
        double Percentile(double p) => samples[Math.Min(samples.Count - 1, (int)(p * samples.Count))];
        var binary = typeof(LiteDatabase).Assembly.Location;
        Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new
        {
            worker, role, readers, warmupMs, warmupCount, measureMs, count = samples.Count, revision,
            activeMs, activeCpuMs, activeAllocated, lifecycleCpuMs, lifecycleAllocated,
            meanMs = samples.Average(), p50Ms = Percentile(.5), p95Ms = Percentile(.95), p99Ms = Percentile(.99),
            worstMs = samples.Last(), chronologicalWindows, checkpointCount, checkpointMs,
            closeMs = close.Elapsed.TotalMilliseconds,
            peakWorkingSetBytes = process.PeakWorkingSet64, idleWorkingSetBytes = process.WorkingSet64,
            idleThreads = process.Threads.Count, idleHandles = process.HandleCount, retainedManagedBytes = GC.GetTotalMemory(false),
            runtime = RuntimeInformation.FrameworkDescription, os = RuntimeInformation.OSDescription,
            binary, sha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(binary)))
        }));
    }

    private static int Check(BsonDocument row, int id, int? revision)
    {
        if (row == null || row.Count != 3 || row["_id"].AsInt32 != id) throw new InvalidOperationException("Missing or malformed row " + id);
        var actual = row["revision"].AsInt32;
        if (actual < 0 || revision.HasValue && actual != revision.Value || row["payload"].AsString != Payload(id, actual))
            throw new InvalidOperationException("Inconsistent full document " + id);
        return actual;
    }

    private static int Validate(IEnumerable<BsonDocument> documents, int expectedCount, int? revision)
    {
        var seen = new HashSet<int>();
        foreach (var row in documents)
        {
            var id = row["_id"].AsInt32;
            if (id < 0 || id >= Rows || !seen.Add(id)) throw new InvalidOperationException("Invalid scan identity");
            revision = Check(row, id, revision);
        }
        if (seen.Count != expectedCount) throw new InvalidOperationException("Incomplete scan");
        return revision.Value;
    }
}
