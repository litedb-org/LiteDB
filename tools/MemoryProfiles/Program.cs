using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using LiteDB;

if (args.Length < 3 || args.Length > 5)
{
    Console.Error.WriteLine("Usage: MemoryProfiles <label> <profile> <output.json> [cache-MiB] [transaction-pages]");
    return 1;
}

var report = new ProfileReport(args[0], args[1]);
var cacheMiB = args.Length > 3 ? int.Parse(args[3], CultureInfo.InvariantCulture) : 0;
int? transactionPages = args.Length > 4 ? int.Parse(args[4], CultureInfo.InvariantCulture) : null;
using (var workload = new ProfileWorkload(report, cacheMiB, transactionPages)) workload.Run();
File.WriteAllText(args[2], System.Text.Json.JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine(args[2]);
return 0;

internal sealed class ProfileReport
{
    public string Label { get; }
    public string Profile { get; }
    public string Runtime { get; } = RuntimeInformation.FrameworkDescription;
    public string OS { get; } = RuntimeInformation.OSDescription;
    public int Processors { get; } = Environment.ProcessorCount;
    public string AssemblySha256 { get; } = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(typeof(LiteDatabase).Assembly.Location)));
    public Dictionary<string, object> Measurements { get; } = new Dictionary<string, object>();

    public ProfileReport(string label, string profile)
    {
        this.Label = label;
        this.Profile = profile;
    }

    public void Measure(string name, Action action)
    {
        var watch = Stopwatch.StartNew();
        action();
        watch.Stop();
        this.Measurements[name + "Ms"] = watch.Elapsed.TotalMilliseconds;
        Console.WriteLine($"{name}: {watch.Elapsed.TotalMilliseconds:F1} ms");
    }
}
