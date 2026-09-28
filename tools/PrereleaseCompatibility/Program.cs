using System;
using System.IO;
using System.Linq;
using System.Reflection;
using LiteDB;

// docs/rules/compatibility.md#published-prereleases: a file written by any published
// LiteDB opens correctly (read, or migrate on a writable open) or is refused with its
// data file and WAL byte-identical; it is never misread or modified by a refused open.
//   create <dir>             (compiled against a published package) writes four fixtures;
//   check  <dir> [must-open] (compiled against the current source) classifies each of them.
// Only documented refusals count as safe: an unsupported format version, or (read-only)
// the migration-required refusal. Stable releases must also open writable (must-open).
var mode = args[0];
var directory = args[1];
var mustOpen = args.Length > 2 && args[2] == "must-open";
var loaded = typeof(LiteDatabase).Assembly;
Console.WriteLine($"LiteDB loaded: {loaded.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion}");
// Published prereleases refuse to open until the caller acknowledges the risk (#3038);
// stable packages have no such pragma, so it is looked up by name.
loaded.GetType("LiteDB.LiteDBPragmas")?.GetMethod("I_AM_AWARE_MY_DATABASE_BREAKS_WHEN_I_USE_THIS")?.Invoke(null, null);

foreach (var encrypted in new[] { false, true })
foreach (var dirty in new[] { false, true })
{
    var name = $"{(encrypted ? "encrypted" : "plain")}-{(dirty ? "wal" : "clean")}";
    var path = Path.Combine(directory, name + ".db");
    var logPath = Path.Combine(directory, name + "-log.db");
    var password = encrypted ? "prerelease-compatibility" : null;
    if (mode == "create")
    {
        using var database = Open(path, password, readOnly: false);
        database.CheckpointSize = 0;
        var rows = database.GetCollection("rows");
        rows.Insert(Documents(0));
        rows.EnsureIndex("bucket");
        database.GetCollection("unrelated").Insert(Documents(7));
        database.Checkpoint();
        rows.Update(Documents(1));
        if (!dirty) database.Checkpoint();
        continue;
    }
    Require(mode == "check", "Unknown mode.");
    Console.WriteLine($"{name}: {Classify(path, logPath, password, mustOpen)}");
}

static string Classify(string path, string logPath, string password, bool mustOpen)
{
    var data = File.ReadAllBytes(path);
    var log = BytesOrNull(logPath);
    var readOnly = Attempt(path, password, readOnly: true, out var readOnlyError);
    Require(data.SequenceEqual(File.ReadAllBytes(path)) && SameBytes(log, BytesOrNull(logPath)),
        $"A read-only open changed the files ({(readOnly ? "opened" : "refused: " + readOnlyError)}).");
    var writable = Attempt(path, password, readOnly: false, out var writableError);
    Require(writable || !mustOpen, $"A stable release's file must open writable, but it was refused: {writableError}");
    if (!writable)
    {
        Require(data.SequenceEqual(File.ReadAllBytes(path)) && SameBytes(log, BytesOrNull(logPath)),
            $"A refused writable open changed the files: {writableError}");
        return readOnly ? $"read (writable open refused without changes: {writableError})"
                        : $"refused without changes: {writableError}";
    }
    using (var database = Open(path, password, readOnly: false))
        database.GetCollection("rows").Insert(new BsonDocument { ["_id"] = 1000, ["bucket"] = 0 });
    using (var database = Open(path, password, readOnly: true))
        VerifyContents(database, new BsonDocument { ["_id"] = 1000, ["bucket"] = 0 });
    return readOnly ? "read, then written and reopened"
                    : $"migrated on a writable open (read-only open refused: {readOnlyError}), then written and reopened";
}

// Opens and verifies complete payloads and index results; false (with the reason) on a clean refusal.
static bool Attempt(string path, string password, bool readOnly, out string refusal)
{
    refusal = null;
    try
    {
        using var database = Open(path, password, readOnly);
        VerifyContents(database, null);
        return true;
    }
    catch (LiteException error) when (Documented(error, readOnly))
    {
        refusal = $"LiteException {error.ErrorCode}: {error.Message}";
        return false;
    }
}

// Any other LiteException (integrity, checksum, invalid database...) propagates and fails the check.
static bool Documented(LiteException error, bool readOnly) =>
    error.ErrorCode == 138 || // LiteException.UNSUPPORTED_FILE_VERSION; older packages lack the constant

    (readOnly && error.Message.IndexOf("requires migration", StringComparison.OrdinalIgnoreCase) >= 0
              && error.Message.IndexOf("writable", StringComparison.OrdinalIgnoreCase) >= 0);

// Complete payloads, the untouched collection and every indexed bucket, plus an optional added document.
static void VerifyContents(LiteDatabase database, BsonDocument added)
{
    var expected = added == null ? Documents(1) : Documents(1).Append(added).ToArray();
    var rows = database.GetCollection("rows");
    Verify(rows.FindAll().ToArray(), expected);
    Verify(database.GetCollection("unrelated").FindAll().ToArray(), Documents(7));
    foreach (var bucket in Enumerable.Range(0, 7))
        Verify(rows.Find(Query.EQ("bucket", bucket)).ToArray(),
            expected.Where(document => document["bucket"].AsInt32 == bucket).ToArray());
}

static LiteDatabase Open(string path, string password, bool readOnly) =>
    new LiteDatabase(new ConnectionString { Filename = path, Password = password, ReadOnly = readOnly });

static BsonDocument[] Documents(int generation) => Enumerable.Range(1, 48).Select(id => new BsonDocument
{
    ["_id"] = id, ["bucket"] = id % 7, ["generation"] = generation,
    ["name"] = $"document-{id}-generation-{generation}",
    ["text"] = new string((char)('a' + id % 20), 400),
    ["nested"] = new BsonDocument { ["id"] = id, ["generation"] = generation },
    ["binary"] = new byte[] { (byte)id, (byte)generation, 0, 255 }
}).ToArray();

static void Verify(BsonDocument[] actual, BsonDocument[] expected)
{
    Require(actual.Length == expected.Length, $"Misread: {actual.Length} documents instead of {expected.Length}.");
    var ordered = actual.OrderBy(document => document["_id"].AsInt32).ToArray();
    for (var index = 0; index < expected.Length; index++)
        Require(BsonSerializer.Serialize(ordered[index]).SequenceEqual(BsonSerializer.Serialize(expected[index])),
            $"Misread: document {expected[index]["_id"]} differs from what was written.");
}

static byte[] BytesOrNull(string path) => File.Exists(path) ? File.ReadAllBytes(path) : null;
static bool SameBytes(byte[] left, byte[] right) => left == null ? right == null : right != null && left.SequenceEqual(right);
static void Require(bool condition, string message) { if (!condition) throw new Exception(message); }
