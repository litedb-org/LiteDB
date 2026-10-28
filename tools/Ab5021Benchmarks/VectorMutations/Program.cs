using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using LiteDB;
using LiteDB.Vector;

// Report-only production measurements. Deliberately refuse local execution.
if (Environment.GetEnvironmentVariable("GITHUB_ACTIONS") != "true")
    throw new InvalidOperationException("Run this workload in GitHub Actions only.");
var count = args.Length > 0 ? int.Parse(args[0]) : 10000;
var operations = args.Length > 1 ? int.Parse(args[1]) : 100;
var output = Path.GetFullPath(args.Length > 2 ? args[2] : "vector-mutations.json");
if (count < 100 || operations < 1 || operations > count / 2) throw new ArgumentOutOfRangeException(nameof(count));
var root = Path.Combine(Path.GetTempPath(), "litedb-vector-mutations-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
try
{
    Run(100, 10, Path.Combine(root, "warmup.db"));
    var samples = Run(count, operations, Path.Combine(root, "measured.db"));
    var assembly = typeof(LiteDatabase).Assembly;
    Directory.CreateDirectory(Path.GetDirectoryName(output)!);
    File.WriteAllText(output, JsonSerializer.Serialize(new
    {
        count, operations, dimensions = 64, seed = 3142,
        reportOnly = true,
        runtime = RuntimeInformation.FrameworkDescription,
        os = RuntimeInformation.OSDescription,
        framework = AppContext.TargetFrameworkName,
        sourceCommit = Environment.GetEnvironmentVariable("BENCHMARK_SOURCE_SHA"),
        candidateCommit = Environment.GetEnvironmentVariable("BENCHMARK_CANDIDATE_SHA"),
        assembly = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion,
        assemblySha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(assembly.Location))),
        samples
    }, new JsonSerializerOptions { WriteIndented = true }));
}
finally { Directory.Delete(root, true); }

static object Run(int count, int operations, string filename)
{
    using var db = new LiteDatabase(filename);
    db.CheckpointSize = 0;
    var docs = db.GetCollection<VectorDocument>("vectors");
    docs.Insert(Enumerable.Range(1, count).Select(Create));
    var samples = new List<object>();
    Measure("vector-build", 1, () => docs.EnsureIndex("embedding_idx", "$.Embedding",
        new VectorIndexOptions(64, VectorDistanceMetric.Euclidean)));
    db.Checkpoint();
    Measure("vector-insert", operations, () =>
    {
        for (var i = 1; i <= operations; i++) docs.Insert(Create(count + i));
    });
    Measure("vector-update", operations, () =>
    {
        for (var i = 1; i <= operations; i++)
        {
            var document = Create(i);
            document.Embedding = Create(-i).Embedding;
            if (!docs.Update(document)) throw new InvalidOperationException("Update lost a document.");
        }
    });
    Measure("vector-delete", operations, () =>
    {
        for (var i = 1; i <= operations; i++)
            if (!docs.Delete(i)) throw new InvalidOperationException("Delete lost a document.");
    });
    var expected = Enumerable.Range(operations + 1, count).ToArray();
    if (!docs.FindAll().Select(x => x.Id).OrderBy(x => x).SequenceEqual(expected))
        throw new InvalidOperationException("Mutation identity oracle failed.");
    var target = Create(0).Embedding;
    var exact = docs.Query().WhereNear(x => x.Embedding, target, double.MaxValue).ToArray();
    if (exact.Length != count) throw new InvalidOperationException("Exact scan omitted surviving documents.");
    var deferred = false;
    try
    {
        var ann = docs.Query().TopKNear(x => x.Embedding, target, 10).ToArray();
        if (ann.Length != 10 || ann.Any(x => x.Id <= operations))
            throw new InvalidOperationException("ANN returned stale/missing documents.");
    }
    catch (LiteException error) when (error.Message.Contains("Vector index repair deferred"))
    {
        deferred = true; // Visible correctness-preserving fallback is reported.
    }
    return new { samples, deferred, exactCount = exact.Length };

    void Measure(string scenario, int items, Action action)
    {
        var allocated = GC.GetTotalAllocatedBytes();
        var watch = Stopwatch.StartNew();
        action();
        watch.Stop();
        samples.Add(new
        {
            scenario, operations = items, milliseconds = watch.Elapsed.TotalMilliseconds,
            allocatedBytes = GC.GetTotalAllocatedBytes() - allocated,
            dataBytes = new FileInfo(filename).Length,
            directoryBytes = Directory.GetFiles(Path.GetDirectoryName(filename)!).Sum(path => new FileInfo(path).Length)
        });
    }
}

static VectorDocument Create(int id)
{
    var random = new Random(unchecked(3142 + id * 7919));
    return new VectorDocument { Id = id, Embedding = Enumerable.Range(0, 64).Select(_ => (float)random.NextDouble()).ToArray() };
}

public sealed class VectorDocument
{
    public int Id { get; set; }
    public float[] Embedding { get; set; } = Array.Empty<float>();
}
