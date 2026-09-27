using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using LiteDB;
using LiteDB.Engine;
using LiteDB.Vector;

var mode = args[0];
var directory = args[1];
var maximum = typeof(LiteDatabase).Assembly.GetType("LiteDB.Engine.HeaderPage")
    .GetFields(BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)
    .Where(field => field.IsLiteral && field.FieldType == typeof(byte) && field.Name.EndsWith("FILE_VERSION"))
    .Max(field => Convert.ToInt32(field.GetRawConstantValue()));
Require(maximum == (mode == "create" || mode == "reject" ? 9 : 13), "Unexpected engine format capability.");
Console.WriteLine($"Runtime: {System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription}; engine: {typeof(LiteDatabase).Assembly.FullName}");
var attempts = 0;
foreach (var encrypted in new[] { false, true })
foreach (var dirty in new[] { false, true })
{
    var password = encrypted ? "v9-bridge" : null;
    var stem = $"v9-{(encrypted ? "encrypted" : "plain")}-{(dirty ? "wal" : "clean")}";
    var original = Path.Combine(directory, stem + ".db");
    if (mode == "create")
    {
        using (var db = Open(original, password))
        {
            db.CheckpointSize = 0;
            var rows = db.GetCollection("rows");
            rows.Insert(Documents(0));
            rows.EnsureIndex("bucket");
            rows.EnsureIndex("embedding", "$.Embedding", new VectorIndexOptions(2, VectorDistanceMetric.Euclidean));
            db.GetCollection("cold").Insert(new BsonDocument { ["_id"] = 1, ["payload"] = "preserve" });
            db.Checkpoint();
            rows.Update(Documents(1));
            if (!dirty) db.Checkpoint();
            Verify(db, 9);
        }
        Require(Header(original, password)[59] == 9, "Historical writer did not persist v9.");
        Require(dirty == (WalLength(original, encrypted) > 0), "Historical WAL fixture state is wrong.");
        continue;
    }
#if CURRENT
    if (mode == "upgrade")
    {
        Require(Header(original, password)[59] == 9, "Upgrade input must be genuine persisted v9.");
        var before = Bytes(original);
        var beforeLog = Bytes(Log(original));
        var rejected = false;
        try { using var readOnly = Open(original, password, readOnly: true); }
        catch (LiteException error) when (error.Message.Contains("requires migration")) { rejected = true; }
        Require(rejected && Same(before, Bytes(original)) && Same(beforeLog, Bytes(Log(original))),
            "Read-only pending migration must reject without mutation.");
        using var engine = new LiteEngine(new EngineSettings { Filename = original, Password = password });
        using var db = new LiteDatabase(engine, disposeOnClose: false);
        Verify(db, 11);
        SaveStage(original, password, 11);
        db.GetCollection("hot").Insert(Hot(0));
        Require(Header(original, password)[59] == 12, "Compact writes did not publish v12.");
        SaveStage(original, password, 12);
        for (var generation = 1; generation <= 5; generation++) db.GetCollection("hot").Update(Hot(generation));
        using (var reader = engine.Query("hot", new Query()))
        {
            Task.Run(() =>
            {
                for (var generation = 6; generation <= 9; generation++) db.GetCollection("hot").Update(Hot(generation));
                db.Checkpoint();
            }).GetAwaiter().GetResult();
            var header = Header(original, password);
            Require(header[59] == 13 && BitConverter.ToInt64(header, 168) > 0,
                "Fixture must publish a real v13 retirement root.");
            SaveStage(original, password, 13);
            var seen = 0;
            while (reader.Read()) { Require(reader.Current["generation"].AsInt32 == 5, "Snapshot changed."); seen++; }
            Require(seen == 16, "Snapshot lost rows.");
        }
        continue;
    }
#endif
    foreach (var version in new[] { 11, 12, 13 })
    foreach (var wal in new[] { false, true })
    {
        var path = Stage(original, version, wal);
        Require(Header(path, password)[59] == version, "Stage header version is wrong.");
        if (mode == "reject")
        {
            Require(wal == (WalLength(path, encrypted) > 0), "Stage WAL state is wrong.");
            var before = Bytes(path);
            var beforeLog = Bytes(Log(path));
            foreach (var connection in new[] { ConnectionType.Direct, ConnectionType.Shared })
            foreach (var access in new[] { "read-only", "write", "upgrade", "rebuild" })
            {
                var rejected = false;
                try
                {
                    using var db = new LiteDatabase(new ConnectionString { Filename = path, Password = password,
                        Connection = connection, ReadOnly = access == "read-only", Upgrade = access == "upgrade" });
                    if (access == "rebuild") db.Rebuild();
                    _ = db.GetCollection("rows").Count();
                }
                catch (LiteException error) when (error.Message.IndexOf("version", StringComparison.OrdinalIgnoreCase) >= 0)
                { rejected = true; }
                Require(rejected && Same(before, Bytes(path)) && Same(beforeLog, Bytes(Log(path))),
                    $"v9 reader changed or accepted v{version}: {connection}/{access}.");
                attempts++;
            }
        }
        else
        {
            Require(mode == "verify", "Unknown mode.");
            foreach (var readOnly in new[] { true, false, true })
            {
                var before = Bytes(path);
                var beforeLog = Bytes(Log(path));
                using (var db = Open(path, password, readOnly)) { Verify(db, version); if (!readOnly) db.Checkpoint(); }
                if (readOnly) Require(Same(before, Bytes(path)) && Same(beforeLog, Bytes(Log(path))), "Read-only changed sources.");
            }
        }
    }
}
Console.WriteLine($"Actual v9 bridge: {mode} passed (maximum format {maximum}, rejection attempts {attempts}).");

static BsonDocument[] Documents(int generation) => Enumerable.Range(1, 48).Select(id => new BsonDocument
{
    ["_id"] = id, ["bucket"] = id % 7, ["generation"] = generation,
    ["Embedding"] = new BsonVector(new[] { (float)id, 1f }),
    ["payload"] = new string((char)('a' + id % 20), 400) + generation,
    ["nested"] = new BsonDocument { ["id"] = id, ["generation"] = generation }
}).ToArray();
static BsonDocument[] Hot(int generation) => Enumerable.Range(1, 16).Select(id => new BsonDocument
{
    ["_id"] = id, ["generation"] = generation, ["payload"] = new string('x', 1500),
    ["long_repeated_array_property"] = new BsonArray(Enumerable.Range(0, 200).Select(value => new BsonValue(value)))
}).ToArray();
static void Verify(LiteDatabase db, int version)
{
    var rows = db.GetCollection("rows");
    Equal(rows.FindAll().ToArray(), Documents(1));
    for (var bucket = 0; bucket < 7; bucket++)
        Equal(rows.Find(Query.EQ("bucket", bucket)).ToArray(), Documents(1).Where(doc => doc["bucket"].AsInt32 == bucket).ToArray());
    Require(rows.Query().Where("$.bucket = 1").GetPlan()["index"]["name"].AsString == "bucket", "Scalar index not selected.");
    Require(rows.Query().TopKNear("Embedding", new[] { 1f, 1f }, 1).GetPlan()["index"]["name"].AsString == "embedding",
        "Vector index not selected.");
    foreach (var doc in Documents(1))
    {
        var nearest = rows.Query().TopKNear("Embedding", doc["Embedding"].AsVector, 1).ToArray();
        Require(nearest.Length == 1 && nearest[0]["_id"] == doc["_id"], "Vector self-neighbor changed.");
    }
    Require(db.GetCollection("cold").FindById(1)["payload"].AsString == "preserve", "Unrelated data changed.");
    if (version >= 12) Equal(db.GetCollection("hot").FindAll().ToArray(), Hot(version == 12 ? 0 : 9));
}
static void Equal(BsonDocument[] actual, BsonDocument[] expected)
{
    Require(actual.Length == expected.Length, "Document count changed.");
    var ordered = actual.OrderBy(doc => doc["_id"].AsInt32).ToArray();
    for (var i = 0; i < expected.Length; i++) Require(BsonSerializer.Serialize(ordered[i]).SequenceEqual(BsonSerializer.Serialize(expected[i])), "Full payload changed.");
}
#if CURRENT
static void SaveStage(string source, string password, int version)
{
    foreach (var wal in new[] { true, false })
    {
        var target = Stage(source, version, wal);
        // All commits/checkpoints have returned and no worker is writing. Copy
        // the quiescent pair with sharing compatible with the live engine handles.
        CopySource(source, target);
        if (File.Exists(Log(source))) CopySource(Log(source), Log(target));
        if (!wal) { using var db = Open(target, password); Verify(db, version); db.Checkpoint(); }
        Require(Header(target, password)[59] == version, "Stage publication failed.");
        Require(wal == (WalLength(target, password != null) > 0), "Stage must exercise requested WAL state.");
    }
}
static void CopySource(string source, string target)
{
    using var reader = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
    using var writer = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None);
    reader.CopyTo(writer);
}
#endif
static LiteDatabase Open(string path, string password, bool readOnly = false) => new LiteDatabase(new ConnectionString
    { Filename = path, Password = password, ReadOnly = readOnly });
static string Stage(string path, int version, bool wal) => Path.ChangeExtension(path, null) + $"-v{version}-{(wal ? "wal" : "clean")}.db";
static string Log(string path) => Path.ChangeExtension(path, null) + "-log.db";
static byte[] Bytes(string path) => File.Exists(path) ? File.ReadAllBytes(path) : null;
static bool Same(byte[] left, byte[] right) => left == null ? right == null : right != null && left.SequenceEqual(right);
static long WalLength(string path, bool encrypted) => File.Exists(Log(path)) ? new FileInfo(Log(path)).Length - (encrypted ? 8192 : 0) : 0;
static byte[] Header(string path, string password)
{
    using var raw = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
    using var stream = password == null ? (Stream)raw : new AesStream(password, raw);
    var header = new byte[8192];
    stream.ReadExactly(header);
    return header;
}
static void Require(bool condition, string message) { if (!condition) throw new Exception(message); }
