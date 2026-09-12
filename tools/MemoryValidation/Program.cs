using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using LiteDB;

// Run in a fresh process for each assembly/runtime/cache profile. A DLL reference
// lets the identical workload exercise the pre-feature engine as well.
if (args.Length != 4)
{
    Console.Error.WriteLine("Usage: MemoryValidation <label> <cache-MiB> <documents> <output.json>");
    return 1;
}

var label = args[0];
var cacheMiB = int.Parse(args[1], CultureInfo.InvariantCulture);
var count = int.Parse(args[2], CultureInfo.InvariantCulture);
var report = new MeasurementReport(label, cacheMiB, count);
using (var workload = new MemoryWorkload(cacheMiB, count, report)) workload.Run();
File.WriteAllText(args[3], System.Text.Json.JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine(args[3]);
return 0;

internal sealed class MeasurementReport
{
    public string Label { get; }
    public string Runtime { get; } = RuntimeInformation.FrameworkDescription;
    public string OS { get; } = RuntimeInformation.OSDescription;
    public int ProcessorCount { get; } = Environment.ProcessorCount;
    public string Assembly { get; } = typeof(LiteDatabase).Assembly.Location;
    public string AssemblySha256 { get; } = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(typeof(LiteDatabase).Assembly.Location)));
    public int CacheMiB { get; }
    public int Documents { get; }
    public Dictionary<string, object> Measurements { get; } = new Dictionary<string, object>();

    public MeasurementReport(string label, int cacheMiB, int documents)
    {
        this.Label = label;
        this.CacheMiB = cacheMiB;
        this.Documents = documents;
    }

    public void Measure(string name, Action action)
    {
        var stopwatch = Stopwatch.StartNew();
        action();
        stopwatch.Stop();
        this.Measurements[name + "Ms"] = stopwatch.Elapsed.TotalMilliseconds;
        Console.WriteLine($"{name}: {stopwatch.Elapsed.TotalMilliseconds:F1} ms");
    }

    public void Latency(string name, long[] ticks)
    {
        Array.Sort(ticks);
        this.Measurements[name] = new
        {
            Samples = ticks.Length,
            P50Microseconds = ticks[(ticks.Length - 1) / 2] * 1e6 / Stopwatch.Frequency,
            P99Microseconds = ticks[(int)Math.Ceiling(ticks.Length * 0.99) - 1] * 1e6 / Stopwatch.Frequency
        };
    }
}
