using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using LiteDB;

if (Environment.GetEnvironmentVariable("GITHUB_ACTIONS") != "true")
    throw new InvalidOperationException("Measurements run only on GitHub CI.");
if (typeof(LiteDatabase).Assembly.GetType("LiteDB.Engine.LiteEngine")!
    .GetMethod("GetMonitor", BindingFlags.Instance | BindingFlags.NonPublic) != null)
    throw new InvalidOperationException("Production library required.");
var mode = args[0];
var root = Path.GetFullPath(args[1]);
var scenario = args[2];
var workers = int.Parse(args[3]);
var requests = int.Parse(args[4]);
Directory.CreateDirectory(root);
var path = Path.Combine(root, "requests.db");
if (mode == "seed")
{
    Seed(path);
}
else if (mode == "verify")
{
    using var db = Open(path);
    var col = db.GetCollection("docs");
    Require(col.Count() == 1000 + (scenario == "insert" ? workers * requests : 0), "row count");
    for (var id = 1; id <= 1000; id++)
    {
        var doc = col.FindById(id);
        Require(doc != null && doc["payload"].AsString == new string('x', 96), "seed payload");
        Require(doc!["value"].AsInt32 == (scenario == "mixed" && id <= workers ? requests : 0), "update value");
    }
    if (scenario == "insert")
        for (var id = 1001; id <= 1000 + workers * requests; id++)
            Require(col.FindById(id)?["value"].AsInt32 == id, "insert identity/value");
    File.WriteAllText(Path.Combine(root, "verified"), "ok");
}
else if (mode == "worker")
{
    var worker = int.Parse(args[5]);
    var warmPath = Path.Combine(root, $"warm-{worker}.db");
    Seed(warmPath);
    for (var i = 0; i < 10; i++) Request(warmPath, i);
    File.WriteAllText(Path.Combine(root, $"ready-{worker}"), "ready");
    var barrier = Stopwatch.StartNew();
    while (!File.Exists(Path.Combine(root, "go")))
    {
        if (barrier.Elapsed.TotalSeconds > 120) throw new TimeoutException("start barrier");
        Thread.Sleep(1);
    }
    var start = DateTime.UtcNow;
    var clock = Stopwatch.StartNew();
    for (var i = 0; i < requests; i++) Request(path, i);
    clock.Stop();
    var end = DateTime.UtcNow;
    var assembly = typeof(LiteDatabase).Assembly;
    File.WriteAllText(Path.Combine(root, $"done-{worker}.json"), System.Text.Json.JsonSerializer.Serialize(new
    {
        worker, requests, scenario, workers, milliseconds = clock.Elapsed.TotalMilliseconds, start, end,
        runtime = RuntimeInformation.FrameworkDescription, framework = AppContext.TargetFrameworkName,
        os = RuntimeInformation.OSDescription, architecture = RuntimeInformation.ProcessArchitecture.ToString(),
        assemblyVersion = assembly.GetName().Version!.ToString(),
        assemblySha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(assembly.Location))),
        candidateCommit = Environment.GetEnvironmentVariable("BENCHMARK_CANDIDATE_SHA"),
        harnessCommit = Environment.GetEnvironmentVariable("BENCHMARK_HARNESS_SHA"),
        tieredCompilation = Environment.GetEnvironmentVariable("DOTNET_TieredCompilation"),
        tempPath = Path.GetTempPath()
    }));
    void Request(string filename, int iteration)
    {
        using var db = Open(filename);
        var col = db.GetCollection("docs");
        if (scenario == "read")
        {
            var id = 100 + (iteration + worker) % 900;
            Require(col.FindById(id)?["value"].AsInt32 == 0, "point read");
        }
        else if (scenario == "insert")
        {
            var id = 1001 + worker * requests + iteration;
            Require(col.Insert(new BsonDocument { ["_id"] = id, ["value"] = id }).AsInt32 == id, "insert");
        }
        else if (scenario == "mixed")
        {
            for (var id = 1; id <= 10; id++)
                Require(col.FindById(id)?["payload"].AsString == new string('x', 96), "mixed read");
            // Each process owns one update row; no application-level read/modify/write race.
            Require(col.Update(new BsonDocument
            {
                ["_id"] = worker + 1, ["value"] = iteration + 1, ["payload"] = new string('x', 96)
            }), "update");
        }
        else throw new ArgumentException("scenario");
    }
}
else throw new ArgumentException("mode");

static LiteDatabase Open(string filename)
{
    var connection = new ConnectionString { Filename = filename, Connection = ConnectionType.Shared,
        Collation = new Collation(127, System.Globalization.CompareOptions.Ordinal) };
#if DEV
    Require(connection.SharedOneFile && connection.DurableCommits, "v6 defaults required");
#endif
    return new LiteDatabase(connection);
}
static void Seed(string filename)
{
    using var db = Open(filename);
    Require(db.CheckpointSize == 1000, "default checkpoint");
    Require(db.GetCollection("docs").Insert(Enumerable.Range(1, 1000).Select(id => new BsonDocument
    {
        ["_id"] = id, ["value"] = 0, ["payload"] = new string('x', 96)
    })) == 1000, "seed count");
}
static void Require(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}
