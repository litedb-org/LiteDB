using System;
using System.Globalization;
using System.IO;
using System.Text;
using LiteDB;

// One workload per process; prints one JSON record. Without the HANDLES constant the runner
// uses only APIs that predate transaction handles, so the identical source measures a
// baseline and a candidate library. Handle workloads exist only in a -p:Handles=true build.
// Usage: TransactionPathBenchmarks <scratch-dir> <workload> [warmup-seconds] [windows] [window-seconds]
//        TransactionPathBenchmarks --child-contender <database-file>  (internal: shared-contention-2proc)
Bench.AcknowledgePrerelease();
if (args.Length == 2 && args[0] == Contention.ChildFlag) return Contention.RunChild(args[1]);
if (args.Length < 2) throw new ArgumentException("Usage: <scratch-dir> <workload> [warmup-seconds] [windows] [window-seconds]");
var settings = new RunSettings(args);
Directory.CreateDirectory(settings.Scratch);
try
{
    Bench.Seed(settings.Connection);
    var record = new JsonRecord().Add("workload", settings.Workload);
    var passed = Resources.TryRun(settings, record, out var resourcePass) ? resourcePass
        : Contention.TryRun(settings, record) || Workloads.RunSingle(settings, record);
    Bench.VerifyRows(settings.Connection);
    Console.WriteLine(record.Add("runtime", Environment.Version.ToString()).ToString());
    // A resource bound that does not hold is a failed workload, but its record is still printed.
    return passed ? 0 : 3;
}
finally
{
    try { Directory.Delete(settings.Scratch, true); } catch { }
}

internal sealed class RunSettings
{
    public RunSettings(string[] args)
    {
        Scratch = Path.Combine(args[0], Guid.NewGuid().ToString("N"));
        Workload = args[1];
        Warmup = TimeSpan.FromSeconds(args.Length > 2 ? double.Parse(args[2], CultureInfo.InvariantCulture) : 3);
        Windows = args.Length > 3 ? int.Parse(args[3], CultureInfo.InvariantCulture) : 5;
        Window = TimeSpan.FromSeconds(args.Length > 4 ? double.Parse(args[4], CultureInfo.InvariantCulture) : 1);
        if (Windows < 1 || Window <= TimeSpan.Zero) throw new ArgumentException("Windows and window length must be positive");
        File = Path.Combine(Scratch, "bench.db");
        Shared = Workload.StartsWith("shared-", StringComparison.Ordinal);
        Connection = Bench.Connect(File, Shared);
    }

    public string Scratch { get; }
    public string Workload { get; }
    public TimeSpan Warmup { get; }
    public int Windows { get; }
    public TimeSpan Window { get; }
    public string File { get; }
    public bool Shared { get; }
    public ConnectionString Connection { get; }
}

internal static class Bench
{
    public const int Rows = 1000;

    public static ConnectionString Connect(string file, bool shared) =>
        new ConnectionString { Filename = file, Connection = shared ? ConnectionType.Shared : ConnectionType.Direct };

    /// <summary>
    /// A prerelease-labelled library refuses its first open until this process acknowledges it.
    /// Baseline and candidate are separate builds, so ask the loaded assembly, not this runner.
    /// </summary>
    public static void AcknowledgePrerelease() =>
        typeof(LiteDatabase).Assembly.GetType("LiteDB.LiteDBPragmas")
            ?.GetMethod("I_AM_AWARE_MY_DATABASE_BREAKS_WHEN_I_USE_THIS")?.Invoke(null, null);

    public static void Seed(ConnectionString connection)
    {
        using var seed = new LiteDatabase(connection);
        var rows = seed.GetCollection("rows");
        for (var i = 1; i <= Rows; i++) rows.Insert(new BsonDocument { ["_id"] = i, ["value"] = i });
        seed.Checkpoint();
    }

    /// <summary>A fast but wrong result is not a result: verify every row through a cold reopen.</summary>
    public static void VerifyRows(ConnectionString connection)
    {
        using var cold = new LiteDatabase(connection);
        var rows = cold.GetCollection("rows");
        if (rows.Count() != Rows) throw new InvalidOperationException("Row count changed");
        for (var i = 1; i <= Rows; i += 97)
            if (rows.FindById(i)?["value"].AsInt32 != i) throw new InvalidOperationException("Row value changed");
        for (var i = 1; i <= 32; i++)
            if (rows.FindById(i)?["value"].AsInt32 != i) throw new InvalidOperationException("Contended row value changed");
    }

    public static double Median(double[] values)
    {
        var sorted = (double[])values.Clone();
        Array.Sort(sorted);
        return sorted[sorted.Length / 2];
    }
}

/// <summary>A flat JSON object writer; values are numbers, strings, arrays or nested raw JSON.</summary>
internal sealed class JsonRecord
{
    private readonly StringBuilder _text = new StringBuilder("{");

    public JsonRecord Add(string key, string value) => Raw(key, "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"");
    public JsonRecord Add(string key, double value) => Raw(key, Number(value));
    public JsonRecord Add(string key, long value) => Raw(key, value.ToString(CultureInfo.InvariantCulture));
    public JsonRecord Add(string key, bool value) => Raw(key, value ? "true" : "false");
    public JsonRecord Add(string key, double[] values) => Raw(key, "[" + string.Join(",", Array.ConvertAll(values, Number)) + "]");
    public JsonRecord Add(string key, JsonRecord value) => Raw(key, value.ToString());

    public JsonRecord Raw(string key, string json)
    {
        if (_text.Length > 1) _text.Append(',');
        _text.Append('"').Append(key).Append("\":").Append(json);
        return this;
    }

    public override string ToString() => _text + "}";

    private static string Number(double value) => value.ToString("R", CultureInfo.InvariantCulture);
}
