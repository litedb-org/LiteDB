using System;
using System.IO;
using System.Linq;
using System.Reflection;
using LiteDB;
using LiteDB.Engine;

var mode = args[0];
var engineVersion = int.Parse(args[1]);
var fileVersion = int.Parse(args[2]);
var directory = args[3];
var headerType = typeof(LiteDatabase).Assembly.GetType("LiteDB.Engine.HeaderPage");
var actualMaximum = headerType.GetFields(BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)
    .Where(field => field.IsLiteral && field.FieldType == typeof(byte) && field.Name.EndsWith("FILE_VERSION"))
    .Max(field => Convert.ToInt32(field.GetRawConstantValue()));
Require(actualMaximum == engineVersion, $"Expected a v{engineVersion} DLL, loaded maximum v{actualMaximum}.");
var attempts = 0;
foreach (var encrypted in new[] { false, true })
foreach (var dirty in new[] { false, true })
{
    var path = Path.Combine(directory, $"v{fileVersion}-{(encrypted ? "encrypted" : "plain")}-{(dirty ? "wal" : "clean")}.db");
    var logPath = Path.Combine(directory, Path.GetFileNameWithoutExtension(path) + "-log.db");
    var password = encrypted ? "parent-format-test" : null;
    if (mode == "create")
    {
        using var database = Open(path, password);
        database.CheckpointSize = 0;
        var rows = database.GetCollection("rows");
        rows.Insert(Documents(0));
        rows.EnsureIndex("bucket");
        database.GetCollection("unrelated").Insert(Documents(7));
        database.Checkpoint();
        rows.Update(Documents(1));
        if (!dirty) database.Checkpoint();
    }
    Require(ReadVersion(path, password) == fileVersion, $"Fixture is not persisted v{fileVersion}: {path}");
    var dataBefore = File.ReadAllBytes(path);
    var logBefore = BytesOrNull(logPath);
    var logicalWalLength = (logBefore?.Length ?? 0) - (encrypted && logBefore != null ? 8192 : 0);
    Require(dirty ? logicalWalLength > 0 : logicalWalLength <= 0, "Fixture WAL state differs from requested state.");
    if (mode == "reject")
    {
        foreach (var connection in new[] { ConnectionType.Direct, ConnectionType.Shared })
        foreach (var access in new[] { "read-only", "write", "upgrade", "rebuild" })
        {
            var rejected = false;
            try
            {
                using var database = Open(path, password, connection, access);
                if (access == "rebuild") database.Rebuild();
                // SharedEngine defers opening until the first operation.
                _ = database.GetCollection("rows").Count();
            }
            catch (LiteException error)
            {
                if (error.Message.IndexOf("version", StringComparison.OrdinalIgnoreCase) < 0) throw;
                rejected = true;
            }
            Require(rejected, $"v{engineVersion} accepted v{fileVersion}, {connection}/{access}.");
            Require(dataBefore.SequenceEqual(File.ReadAllBytes(path)), $"Rejection changed data: {connection}/{access}.");
            Require(SameBytes(logBefore, BytesOrNull(logPath)), $"Rejection changed WAL: {connection}/{access}.");
            attempts++;
        }
    }
    else
    {
        Require(mode == "create" || mode == "verify", "Unknown mode.");
        using var database = Open(path, password, access: "read-only");
        Verify(database.GetCollection("rows").FindAll().ToArray(), Documents(1));
        Verify(database.GetCollection("unrelated").FindAll().ToArray(), Documents(7));
        foreach (var bucket in Enumerable.Range(0, 7))
            Verify(database.GetCollection("rows").Find(Query.EQ("bucket", bucket)).ToArray(),
                Documents(1).Where(document => document["bucket"].AsInt32 == bucket).ToArray());
    }
    Require(dataBefore.SequenceEqual(File.ReadAllBytes(path)) && SameBytes(logBefore, BytesOrNull(logPath)),
        "Read-only verification changed data or WAL.");
}
Console.WriteLine($"v{engineVersion} {mode} v{fileVersion}: four fixtures passed; rejection attempts={attempts}.");

static LiteDatabase Open(string path, string password, ConnectionType connection = ConnectionType.Direct, string access = "write") =>
    new LiteDatabase(new ConnectionString
    {
        Filename = path, Password = password, Connection = connection,
        ReadOnly = access == "read-only", Upgrade = access == "upgrade"
    });

static BsonDocument[] Documents(int generation) => Enumerable.Range(1, 48).Select(id => new BsonDocument
{
    ["_id"] = id, ["bucket"] = id % 7, ["generation"] = generation,
    ["long_repeated_property_name"] = $"document-{id}-generation-{generation}",
    ["another_repeated_property_name"] = new string((char)('a' + id % 20), 400),
    ["nested_document_for_complete_payload_check"] = new BsonDocument { ["id"] = id, ["generation"] = generation },
    ["binary_payload"] = new byte[] { (byte)id, (byte)generation, 0, 255 }
}).ToArray();

static void Verify(BsonDocument[] actual, BsonDocument[] expected)
{
    Require(actual.Length == expected.Length, "Document count changed.");
    var ordered = actual.OrderBy(document => document["_id"].AsInt32).ToArray();
    for (var index = 0; index < expected.Length; index++)
        Require(BsonSerializer.Serialize(ordered[index]).SequenceEqual(BsonSerializer.Serialize(expected[index])),
            $"Complete document mismatch at ID {expected[index]["_id"]}.");
}

static int ReadVersion(string path, string password)
{
    using var raw = File.OpenRead(path);
    if (password == null) { raw.Position = 59; return raw.ReadByte(); }
    using var decrypted = new AesStream(password, raw);
    var header = new byte[8192];
    Require(decrypted.Read(header, 0, header.Length) == header.Length, "Incomplete decrypted header.");
    return header[59];
}

static byte[] BytesOrNull(string path) => File.Exists(path) ? File.ReadAllBytes(path) : null;
static bool SameBytes(byte[] left, byte[] right) => left == null ? right == null : right != null && left.SequenceEqual(right);
static void Require(bool condition, string message) { if (!condition) throw new Exception(message); }
