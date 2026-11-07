using System.Diagnostics.Tracing;
using System.Diagnostics;
using LiteDB;

namespace Ab5021Benchmarks;

// Active only in separate diagnostic children. Production timing children have no listener.
internal sealed class Experiment : EventListener
{
    internal static int Pages =>
#if DEV
        int.Parse(Environment.GetEnvironmentVariable("CHECKPOINT_PAGES") ?? "1000");
#else
        1000;
#endif
    internal static readonly List<object> Results = new();
    private static bool Active;
    private static string PathName = "", Scenario = "";
    private static int Checkpoints, Syncs, Window, MaxWindow;
    private static long Peak;
    private static readonly Stopwatch Interval = new();
    private static double MaxMilliseconds;
    private static bool Enabled => Environment.GetEnvironmentVariable("CHECKPOINT_DIAGNOSTICS") == "1";
    protected override void OnEventSourceCreated(EventSource source)
    {
        if (Enabled && source.Name == "LiteDB-CheckpointProfile") EnableEvents(source, EventLevel.Verbose);
    }
    protected override void OnEventWritten(EventWrittenEventArgs e)
    {
        if (!Active) return;
        SampleWal();
        if (e.EventId == 5) Syncs++;
        if (e.EventId == 3 && Convert.ToInt32(e.Payload![0]) > 0)
        {
            Checkpoints++;
            MaxMilliseconds = Math.Max(MaxMilliseconds, Interval.Elapsed.TotalMilliseconds);
            Window = 0;
            Interval.Restart();
        }
    }
    internal static void Start(string path, string scenario)
    {
        if (!Enabled) return;
        Scenario = scenario; PathName = path; Checkpoints = Syncs = Window = MaxWindow = 0; Peak = 0; MaxMilliseconds = 0;
        Active = true; Interval.Restart(); SampleWal();
    }
    private static void SampleWal()
    {
        var wal = System.IO.Path.ChangeExtension(PathName, null) + "-log.db";
        if (File.Exists(wal)) Peak = Math.Max(Peak, new FileInfo(wal).Length);
    }
    internal static void Committed(string path)
    {
        if (!Enabled || !Active) return;
        // Checkpoint runs synchronously inside Insert. A just-checkpointed commit is durable.
        var wal = System.IO.Path.ChangeExtension(path, null) + "-log.db";
        if (File.Exists(wal) && new FileInfo(wal).Length > 0) Window++;
        MaxWindow = Math.Max(MaxWindow, Window); SampleWal();
    }
    internal static void Stop(string path)
    {
        if (!Enabled) return;
        SampleWal(); Active = false;
        MaxMilliseconds = Math.Max(MaxMilliseconds, Interval.Elapsed.TotalMilliseconds);
        Results.Add(new { scenario = Scenario, path = System.IO.Path.GetFileName(path), checkpointPages = Pages,
            checkpoints = Checkpoints, fileSyncs = Syncs, peakWalBytes = Peak,
            maxUnsyncedCommits = MaxWindow, maxIntervalMilliseconds = MaxMilliseconds });
    }
}

internal static class RecoveryExperiment
{
    internal static int Run(string[] args)
    {
        var path = args[1];
        var connection = new ConnectionString { Filename = path, Connection = ConnectionType.Direct,
            Collation = new Collation(127, System.Globalization.CompareOptions.Ordinal) };
#if DEV
        connection.DurableCommits = false;
#endif
        if (args[0] == "--crash-write")
        {
            // Never dispose: the parent kills this process after the acknowledged READY marker.
            var db = new LiteDatabase(connection);
            db.CheckpointSize = Experiment.Pages;
            var col = db.GetCollection<Document>("documents");
            col.EnsureIndex(x => x.Key, true);
            db.Checkpoint();
            var wal = System.IO.Path.ChangeExtension(path, null) + "-log.db";
#if DEV
            const int frame = 8256;
#else
            const int frame = 8192;
#endif
            var count = 0;
            // Leave at most one typical five-page insert below the auto-checkpoint boundary.
            while (!File.Exists(wal) || new FileInfo(wal).Length < (Experiment.Pages - 5L) * frame)
            {
                count++;
                col.Insert(new Document { Id = count, Key = count * 17, Value = count, Payload = new string('x', 96) });
                if (count > Experiment.Pages * 2) throw new Exception("Full WAL was not reached");
            }
            Console.WriteLine($"READY {count} {new FileInfo(wal).Length}"); Console.Out.Flush();
            Thread.Sleep(Timeout.Infinite);
            GC.KeepAlive(db);
            return 1;
        }
        var clock = Stopwatch.StartNew();
        using (var db = new LiteDatabase(connection))
        {
            clock.Stop();
            var expected = int.Parse(args[2]);
            var docs = db.GetCollection<Document>("documents").FindAll().OrderBy(x => x.Id).ToArray();
            if (docs.Length != expected || docs.Where((d, i) => d.Id != i + 1 || d.Key != d.Id * 17 ||
                d.Value != d.Id || d.Payload != new string('x', 96)).Any()) throw new Exception("Recovery contents differ");
            foreach (var id in new[] { 1, expected })
                if (db.GetCollection<Document>("documents").FindOne(Query.EQ("Key", id * 17))?.Id != id)
                    throw new Exception("Recovered index differs");
        }
        Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new { openMilliseconds = clock.Elapsed.TotalMilliseconds,
            verifiedCommits = int.Parse(args[2]), model = "process-death; OS cache survives, not power loss" }));
        return 0;
    }
}
