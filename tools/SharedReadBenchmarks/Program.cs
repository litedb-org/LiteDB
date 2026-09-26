using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using LiteDB;
using LiteDB.Engine;

internal static class Program
{
    private const int Rows = 2000;
    private static readonly string Payload = new string('x', 200);

    // One process per build/mode/scenario. Always creates its own child directory.
    private static void Main(string[] args)
    {
        if (typeof(LiteEngine).GetMethod("GetMonitor",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic) != null)
            throw new InvalidOperationException("Benchmarks require a Release LiteDB assembly with TestingEnabled=false.");

        if (args.Length == 3 && args[0] == "seed")
        {
            using var seed = new LiteDatabase(args[1]);
            seed.GetCollection("rows").InsertBulk(Enumerable.Range(1, Rows).Select(id => Row(id, 0)));
            if (args[2] == "indexed") seed.GetCollection("rows").EnsureIndex("v", "v");
            return;
        }

        if (args.Length == 6 && args[0] == "contention")
        {
            ContentionBenchmarks.Run(args);
            return;
        }
        if (args.Length == 3 && args[0] == "contention-verify")
        {
            ContentionBenchmarks.Verify(args[1], args[2].Split(',').Select(int.Parse).ToArray());
            return;
        }

        if (args.Length == 2 && args[0] == "interop")
        {
            InteropBenchmarks.Run(Path.GetFullPath(args[1]));
            return;
        }

        if ((args.Length != 4 && args.Length != 5) || !int.TryParse(args[3], out var count) || count <= 0 ||
            (args[1] != "shared" && args[1] != "direct") ||
            !new[] { "point", "scan", "mixed", "phases", "slots", "holder",
                "same-key", "random", "buffered", "indexed", "write", "transaction", "balanced", "write-heavy", "churn", "open-close", "checkpoint" }.Contains(args[2]))
            throw new ArgumentException("Usage: SharedReadBenchmarks <scratch-parent> <shared|direct> <scenario> <count> [warmup-seconds|active-slots]");

        if (args[1] != "shared" && new[] { "slots", "holder", "open-close" }.Contains(args[2]))
            throw new ArgumentException("The slots, holder and open-close diagnostics require shared mode.");
        if (args[2] == "slots" && (args.Length != 5 || !int.TryParse(args[4], out var activeSlots) || activeSlots < 1 || activeSlots > 65536))
            throw new ArgumentException("Usage: SharedReadBenchmarks <scratch-parent> shared slots <count> <active-slots:1..65536>");
        var warmupSeconds = args.Length == 5 ? int.Parse(args[4], CultureInfo.InvariantCulture) : 0;
        if (warmupSeconds < 0) throw new ArgumentOutOfRangeException(nameof(warmupSeconds));

        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        var directory = Path.Combine(Path.GetFullPath(args[0]), "shared-read-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            if (args[2] == "slots") SlotBenchmarks.Run(directory, count, warmupSeconds);
            else if (args[2] == "holder") HolderBenchmarks.Run(count);
            else Run(Path.Combine(directory, "bench.db"), args[1], args[2], count, warmupSeconds);
        }
        finally { Directory.Delete(directory, true); }
    }

    private static void Run(string filename, string mode, string scenario, int count, int warmupSeconds)
    {
        // Seed in another process so the first open does not inherit engine JIT/cache
        // state from fixture creation. Filesystem caches are deliberately not reset.
        var startInfo = new ProcessStartInfo("dotnet") { UseShellExecute = false };
        startInfo.ArgumentList.Add(typeof(Program).Assembly.Location);
        startInfo.ArgumentList.Add("seed");
        startInfo.ArgumentList.Add(filename);
        startInfo.ArgumentList.Add(scenario);
        using (var seed = Process.Start(startInfo))
        {
            if (!seed.WaitForExit(30000))
            {
                seed.Kill(entireProcessTree: true);
                seed.WaitForExit(5000);
                throw new TimeoutException("Benchmark fixture creation");
            }
            if (seed.ExitCode != 0) throw new InvalidOperationException("Benchmark fixture creation failed");
        }

        var settings = new EngineSettings { Filename = filename };
        using ILiteEngine engine = mode == "shared" ? new SharedEngine(settings) : new LiteEngine(settings);
        using var db = new LiteDatabase(engine);
        var rows = db.GetCollection("rows");
        var expected = new int[Rows + 1];
        var changesRows = new[] { "mixed", "write", "transaction", "balanced", "write-heavy", "churn", "checkpoint" }.Contains(scenario);
        var minimumWarmup = scenario == "scan" || scenario == "phases" ? 20 : 1000;
        var phases = new long[3];

        void Operation(int i)
        {
            var id = scenario == "same-key" ? 1 : scenario == "random"
                ? (int)((uint)i * 2654435761U % Rows) + 1 : i % Rows + 1;
            if (scenario == "open-close")
            {
                using var connection = new LiteDatabase(new ConnectionString { Filename = filename, Connection = ConnectionType.Shared });
                Validate(connection.GetCollection("rows").FindById(id), id, expected);
            }
            else if (scenario == "churn")
            {
                if (!rows.Delete(id)) throw new InvalidOperationException("Delete missed a row");
                rows.Insert(Row(id, i));
                expected[id] = i;
            }
            else if (scenario == "transaction")
            {
                db.BeginTrans();
                if (!rows.Update(Row(id, i))) throw new InvalidOperationException("Transactional update missed a row");
                if (!db.Commit()) throw new InvalidOperationException("Transaction did not commit");
                expected[id] = i;
            }
            else if (scenario == "write" || scenario == "checkpoint" ||
                scenario == "mixed" && i % 10 == 0 || scenario == "balanced" && i % 2 == 0 ||
                scenario == "write-heavy" && i % 10 != 0)
            {
                if (!rows.Update(Row(id, i))) throw new InvalidOperationException("Update missed a row.");
                expected[id] = i;
                if (scenario == "checkpoint") db.Checkpoint();
            }
            else if (scenario == "point" || scenario == "mixed" || scenario == "same-key" ||
                scenario == "random" || scenario == "balanced" || scenario == "write-heavy")
                Validate(rows.FindById(id), id, expected);
            else if (scenario == "buffered")
            {
                var seen = 0;
                foreach (var row in rows.Query().Where("_id <= 50").OrderBy("_id").ToEnumerable())
                    Validate(row, ++seen, expected);
                if (seen != 50) throw new InvalidOperationException("Incorrect buffered scan count");
            }
            else if (scenario == "indexed")
            {
                var seen = new bool[Rows + 1];
                var total = 0;
                foreach (var row in rows.Query().OrderBy("v").ToEnumerable())
                {
                    var key = row["_id"].AsInt32;
                    Validate(row, key, expected);
                    if (seen[key]) throw new InvalidOperationException("Duplicate indexed result");
                    seen[key] = true;
                    total++;
                }
                if (total != Rows) throw new InvalidOperationException("Incorrect indexed scan count");
            }
            else if (scenario == "scan")
            {
                var seen = 0;
                foreach (var row in rows.FindAll()) Validate(row, ++seen, expected);
                if (seen != Rows) throw new InvalidOperationException("Incorrect scan count.");
            }
            else
            {
                var start = Stopwatch.GetTimestamp();
                var reader = engine.Query("rows", new Query());
                phases[0] += Stopwatch.GetTimestamp() - start;
                try
                {
                    start = Stopwatch.GetTimestamp();
                    var seen = 0;
                    while (reader.Read()) Validate(reader.Current.AsDocument, ++seen, expected);
                    if (seen != Rows) throw new InvalidOperationException("Incorrect phase scan count.");
                    phases[1] += Stopwatch.GetTimestamp() - start;
                }
                finally
                {
                    start = Stopwatch.GetTimestamp();
                    reader.Dispose();
                    phases[2] += Stopwatch.GetTimestamp() - start;
                }
            }
        }

        var cold = Stopwatch.GetTimestamp();
        Operation(0);
        var coldMs = Milliseconds(Stopwatch.GetTimestamp() - cold);
        var warming = Stopwatch.StartNew();
        var warmup = 0;
        while (warmup < minimumWarmup || warming.Elapsed.TotalSeconds < warmupSeconds) Operation(warmup++);
        warming.Stop();
        Array.Clear(phases, 0, phases.Length);
        var samples = new double[count];
        var allocated = GC.GetTotalAllocatedBytes(true);
        var startCpu = Process.GetCurrentProcess().TotalProcessorTime;
        for (var i = 0; i < count; i++)
        {
            var start = Stopwatch.GetTimestamp();
            Operation(i);
            samples[i] = Milliseconds(Stopwatch.GetTimestamp() - start);
        }
        var cpuMs = (Process.GetCurrentProcess().TotalProcessorTime - startCpu).TotalMilliseconds;
        var bytes = GC.GetTotalAllocatedBytes(true) - allocated;
        // Preserve time order for checking that tiering or host load did not make
        // the measured interval drift, before sorting for percentiles.
        var windows = Enumerable.Range(0, Math.Min(10, count)).Select(window =>
        {
            var start = window * count / Math.Min(10, count);
            var end = (window + 1) * count / Math.Min(10, count);
            return samples.Skip(start).Take(end - start).Average();
        }).ToArray();
        Array.Sort(samples);
        // Validate final write state too, outside the timing/allocation interval.
        if (changesRows)
            for (var id = 1; id <= Rows; id++) Validate(rows.FindById(id), id, expected);

        var log = Path.Combine(Path.GetDirectoryName(filename),
            Path.GetFileNameWithoutExtension(filename) + "-log" + Path.GetExtension(filename));
        long LogBytes() => File.Exists(log) ? new FileInfo(log).Length : 0;
        var walBytesBeforeClose = LogBytes();
        var closing = Stopwatch.GetTimestamp();
        db.Dispose();
        engine.Dispose();
        var closeMs = Milliseconds(Stopwatch.GetTimestamp() - closing);
        var walBytesAfterClose = LogBytes();
        if (walBytesAfterClose != 0) throw new InvalidOperationException("Final close left WAL content behind.");

        var binary = typeof(LiteDatabase).Assembly.Location;
        Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new
        {
            mode, scenario, count, warmup, warmupSeconds, warmupMs = warming.Elapsed.TotalMilliseconds,
            coldMs, meanMs = samples.Average(), windows,
            p50Ms = samples[count / 2], p95Ms = samples[Math.Min(count - 1, (int)(count * 0.95))], worstMs = samples[count - 1], p99Ms = samples[Math.Min(count - 1, (int)(count * 0.99))],
            bytesPerOperation = bytes / (double)count, cpuMsPerOperation = cpuMs / count,
            queryMs = Milliseconds(phases[0]) / count,
            iterateMs = Milliseconds(phases[1]) / count,
            disposeMs = Milliseconds(phases[2]) / count,
            closeMs, dataBytes = new FileInfo(filename).Length, walBytesBeforeClose, walBytesAfterClose,
            runtime = RuntimeInformation.FrameworkDescription, os = RuntimeInformation.OSDescription,
            binary, sha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(binary)))
        }));
    }

    private static double Milliseconds(long ticks) => ticks * 1000.0 / Stopwatch.Frequency;

    private static BsonDocument Row(int id, int value) =>
        new BsonDocument { ["_id"] = id, ["v"] = value, ["pad"] = Payload };

    private static void Validate(BsonDocument row, int id, int[] expected)
    {
        if (row == null || id > Rows || row["_id"].AsInt32 != id ||
            row["v"].AsInt32 != expected[id] || row["pad"].AsString != Payload)
            throw new InvalidOperationException("Incorrect document at ID " + id);
    }
}
