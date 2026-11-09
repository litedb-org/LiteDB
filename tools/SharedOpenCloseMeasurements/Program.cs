using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using LiteDB;
using LiteDB.Engine;

if (Environment.GetEnvironmentVariable("GITHUB_ACTIONS") != "true" ||
    Environment.GetEnvironmentVariable("GITHUB_REPOSITORY") != "JKamsker/LiteDB")
    throw new InvalidOperationException("Mapped open/close timing runs only in fork GitHub CI.");
if (typeof(LiteEngine).GetMethod("GetMonitor", BindingFlags.Instance | BindingFlags.NonPublic) != null)
    throw new InvalidOperationException("Timing requires a Release library with TestingEnabled=false.");
if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux) || RuntimeInformation.ProcessArchitecture != Architecture.X64)
    throw new PlatformNotSupportedException("ABI-2 qualification is Linux x64 only.");
if (args.Length != 3) throw new ArgumentException("Expected scratch directory, variant and source SHA.");

var directory = Path.Combine(Path.GetFullPath(args[0]), Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(directory);
try
{
    var filename = Path.Combine(directory, "test.db");
    using (var seed = new LiteDatabase(filename))
    {
        seed.GetCollection("docs").Insert(new BsonDocument { ["_id"] = 1, ["value"] = "acknowledged" });
        seed.GetCollection("docs").EnsureIndex("value");
        seed.GetCollection("sentinel").Insert(new BsonDocument { ["_id"] = 42, ["payload"] = new string('x', 20000) });
    }
    var connection = new ConnectionString { Filename = filename, Connection = ConnectionType.Shared, SharedOneFile = true };
    for (var warmup = 0; warmup < 10; warmup++) Cycle();
    const int cycles = 50;
    var elapsed = Stopwatch.StartNew();
    for (var cycle = 0; cycle < cycles; cycle++) Cycle();
    elapsed.Stop();
    if (!File.Exists(filename + "-shm")) throw new Exception("Mapped authority was not created");
    using (var check = new LiteDatabase(filename))
    {
        var rows = check.GetCollection("docs");
        if (rows.Count() != 1 || rows.FindById(1)["value"].AsString != "acknowledged" ||
            rows.FindOne(Query.EQ("value", "acknowledged"))["_id"].AsInt32 != 1 ||
            check.GetCollection("sentinel").FindById(42)["payload"].AsString != new string('x', 20000))
            throw new Exception("Open/close cycles changed committed data");
    }
    var library = typeof(LiteDatabase).Assembly.Location;
    Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new
    {
        variant = args[1], sourceSha = args[2], runtime = Environment.Version.ToString(),
        os = RuntimeInformation.OSDescription, architecture = RuntimeInformation.ProcessArchitecture.ToString(),
        librarySha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(library))),
        cycles, warmupCycles = 10, tieredCompilation = Environment.GetEnvironmentVariable("DOTNET_TieredCompilation"),
        totalMs = elapsed.Elapsed.TotalMilliseconds,
        perCycleMs = elapsed.Elapsed.TotalMilliseconds / cycles
    }));
    void Cycle() { using var db = new LiteDatabase(connection); }
}
finally { Directory.Delete(directory, true); }
