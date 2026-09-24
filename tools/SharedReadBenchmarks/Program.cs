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

        if ((args.Length != 4 && args.Length != 5) || !int.TryParse(args[3], out var count) || count <= 0 ||
            (args[1] != "shared" && args[1] != "direct") ||
            !new[] { "point", "scan", "mixed", "phases" }.Contains(args[2]))
            throw new ArgumentException("Usage: SharedReadBenchmarks <scratch-parent> <shared|direct> <point|scan|mixed|phases> <count> [warmup-seconds]");

        var warmupSeconds = args.Length == 5 ? int.Parse(args[4], CultureInfo.InvariantCulture) : 0;
        if (warmupSeconds < 0) throw new ArgumentOutOfRangeException(nameof(warmupSeconds));

        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        var directory = Path.Combine(Path.GetFullPath(args[0]), "shared-read-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try { Run(Path.Combine(directory, "bench.db"), args[1], args[2], count, warmupSeconds); }
        finally { Directory.Delete(directory, true); }
    }

    private static void Run(string filename, string mode, string scenario, int count, int warmupSeconds)
    {
        using (var seed = new LiteDatabase(filename))
            seed.GetCollection("rows").InsertBulk(Enumerable.Range(1, Rows).Select(id => Row(id, 0)));

        var settings = new EngineSettings { Filename = filename };
        using ILiteEngine engine = mode == "shared" ? new SharedEngine(settings) : new LiteEngine(settings);
        using var db = new LiteDatabase(engine);
        var rows = db.GetCollection("rows");
        var expected = new int[Rows + 1];
        var minimumWarmup = scenario == "scan" || scenario == "phases" ? 20 : 1000;
        var phases = new long[3];

        void Operation(int i)
        {
            var id = i % Rows + 1;
            if (scenario == "mixed" && i % 10 == 0)
            {
                if (!rows.Update(Row(id, i))) throw new InvalidOperationException("Update missed a row.");
                expected[id] = i;
            }
            else if (scenario == "point" || scenario == "mixed")
                Validate(rows.FindById(id), id, expected);
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
        if (scenario == "mixed")
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
            p50Ms = samples[count / 2], p99Ms = samples[Math.Min(count - 1, (int)(count * 0.99))],
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
