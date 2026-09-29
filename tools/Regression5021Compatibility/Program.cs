using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using LiteDB;

// Black-box checks of the 5.0.21 regression fixes (PR #3027) against a production LiteDB.dll
// (Release, TestingEnabled=false): public API and real files only, no test hooks (issue #3034).
// scripts/test-5021-regression-compatibility.py extracts the fixtures to <fixtures>/<archive>/<entry>;
// each scenario uses fresh copies in <work>. Expected outcomes: RegressionFixtures.md, LiteDB.Tests/Regressions.
var fixtures = Path.GetFullPath(args[0]);
var work = Path.GetFullPath(args[1]);
var engine = typeof(LiteDatabase).Assembly;
Console.WriteLine($"Runtime: {RuntimeInformation.FrameworkDescription}; engine: {engine.FullName}, SHA-256 " +
    Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(engine.Location))).ToLowerInvariant());
var hooks = engine.GetType("LiteDB.Engine.EngineState")?.GetField("SimulateProcessCrash", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
// A development prerelease build (LITEDB_PREDEV) refuses to open files until this is acknowledged.
var preDev = engine.GetType("LiteDB.LiteDBPragmas")?.GetMethod("I_AM_AWARE_MY_DATABASE_BREAKS_WHEN_I_USE_THIS");
preDev?.Invoke(null, null);
Console.WriteLine(hooks != null ? "FAIL: this LiteDB.dll has test hooks (DEBUG or TestingEnabled); build it with -p:TestingEnabled=false."
    : $"Production build without test hooks{(preDev != null ? " (development prerelease, risk acknowledged)" : "")}.");
if (hooks != null) return 2;

var failures = new List<string>();
var copies = 0;
Run("WalCrash: writable open converts and keeps every commit", WalCrashConversion);
Run("WalCrash: read-only legacy scan reads every commit and changes nothing", WalCrashReadOnly);
Run("WalCrash: shared open with a live reader lease refuses the conversion", WalCrashSharedLease);
Run("DropIndex: collections 5.0.21 dropped indexes from stay writable and consistent", DroppedIndexes);
Run("ForeignWal: another database's log fails the open and changes nothing", ForeignWal);
Run("ConcurrentWalCrash: commits above the data file's last page are kept", ConcurrentWalCrash);
Run("DamagedDocument: default open names the damage and only marks the file", DamagedDocumentRefused);
Run("DamagedDocument: auto-rebuild keeps 1, 3 and the readable part of 2", DamagedDocumentSalvaged);
Console.WriteLine(failures.Count == 0 ? "All scenarios passed." : $"{failures.Count} scenario(s) failed: {string.Join("; ", failures)}");
return failures.Count == 0 ? 0 : 1;

void Run(string name, Action scenario)
{
    try { scenario(); Console.WriteLine($"PASS {name}"); }
    catch (Exception ex) { failures.Add(name); Console.WriteLine($"FAIL {name}\n     {ex.GetType().Name}: {ex.Message}"); }
}

// crash.db holds {_id: 0..99, value: 0} with index "value"; its WAL alone holds _id 0..19 -> 7 and _id 100 (7).
void WalCrashConversion()
{
    var data = WalCrash(Fresh());
    Require(File.ReadAllBytes(data)[59] == 8, "the fixture is not a 5.0.21 (file version 8) file");
    for (var open = 1; open <= 2; open++)
    {
        using var db = new LiteDatabase($"Filename={data}");
        CheckWalCrash(db, $"writable open {open}", indexed: true);
    }
    Require(File.ReadAllBytes(data)[59] >= 10, $"the writable opens left file version {File.ReadAllBytes(data)[59]}, not a converted file");
    using var reopened = new LiteDatabase($"Filename={data};ReadOnly=true");
    CheckWalCrash(reopened, "read-only open of the converted file", indexed: true);
}

void WalCrashReadOnly()
{
    var directory = Fresh();
    var data = WalCrash(directory);
    var before = Snapshot(directory);
    using (var db = new LiteDatabase($"Filename={data};ReadOnly=true;Legacy Index Scan=true"))
        CheckWalCrash(db, "read-only legacy scan", indexed: false);
    RequireUnchanged(directory, before, "the read-only legacy scan");
}

// Another process's shared reader holds a snapshot (its lease file is open and locked). The drain
// cannot run past it, so the conversion must be refused with both files unchanged; it succeeded
// by truncating the legacy WAL, losing its commits. Once the reader is gone, the open converts.
void WalCrashSharedLease()
{
    var directory = Fresh();
    var data = WalCrash(directory);
    var lease = Path.Combine(Directory.CreateDirectory(data + "-readers").FullName, "1-live.lease");
    using (new FileStream(lease, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
    {
        var before = Snapshot(directory);
        var seen = "";
        var error = Failure(() =>
        {
            using var db = new LiteDatabase($"Filename={data};Connection=shared");
            var docs = db.GetCollection("docs").FindAll().ToList();
            seen = $"{docs.Count} documents, {docs.Count(x => x["value"] == 7)} with value 7";
        });
        Require(error is LiteException { ErrorCode: LiteException.LOCK_TIMEOUT },
            $"expected LOCK_TIMEOUT ({LiteException.LOCK_TIMEOUT}) while the lease is held; got {Describe(error, seen)} ({Sizes(Snapshot(directory))})");
        RequireUnchanged(directory, before, "the refused conversion");
    }
    using var reopened = new LiteDatabase($"Filename={data};Connection=shared");
    CheckWalCrash(reopened, "shared open after the reader closed", indexed: true);
}

void CheckWalCrash(LiteDatabase db, string stage, bool indexed)
{
    var col = db.GetCollection("docs");
    var all = col.FindAll().ToList();
    var sevens = Ids(all.Where(x => x["value"] == 7));
    Require(Ids(all).SequenceEqual(Enumerable.Range(0, 101)) && all.All(x => x["value"] == 7 || x["value"] == 0) &&
        sevens.SequenceEqual(Enumerable.Range(0, 20).Append(100)),
        $"{stage}: {all.Count} documents, {sevens.Length} with value 7 (expected 101 and 21: _id 0..19 and 100)");
    RequireSame(Ids(col.Find(Query.EQ("value", 7))), sevens, $"{stage}: value = 7");
    RequireSame(Ids(col.Find(Query.EQ("value", 0))), Ids(all.Where(x => x["value"] == 0)), $"{stage}: value = 0");
    RequireIndexes(db, "docs", stage, "_id", "value");
    if (indexed) RequirePlan(col, "$.value", 7, "value", stage);
}

// See LegacyDroppedIndex_Tests: 5.0.21 left the tail of dropped index entries on the collection page.
void DroppedIndexes()
{
    var customers = Put(Fresh(), "DropIndex_5_0_21", "customers.db");
    using (var db = new LiteDatabase(customers))
    {
        var col = db.GetCollection("customers");
        Require(col.Count() == 200, $"customers.db: {col.Count()} documents, expected 200");
        Write(() => col.Insert(new BsonDocument { ["_id"] = 1000, ["Name"] = "x", ["Age"] = 1, ["CustomerId"] = "C1000" }), "customers.db: insert");
        Require(col.Update(new BsonDocument { ["_id"] = 1, ["Name"] = "y", ["Age"] = 2, ["CustomerId"] = "C1" }) && col.Delete(2),
            "customers.db: the update of _id 1 or the delete of _id 2 found no document");
        col.EnsureIndex("Age");
        Require(col.Count(Query.EQ("CustomerId", "C5")) == 1, "customers.db: CustomerId = C5");
        IndexesMatchScan(db, "customers", "customers.db", "_id", "Age", "CustomerId", "Name");
    }
    using (var db = new LiteDatabase(customers))
    {
        var col = db.GetCollection("customers");
        Require(col.Count() == 200 && col.Count(Query.EQ("Age", 2)) == 3, // 92, 182 and the updated 1
            $"customers.db reopened: {col.Count()} documents, {col.Count(Query.EQ("Age", 2))} with Age 2 (expected 200 and 3)");
        IndexesMatchScan(db, "customers", "customers.db reopened", "_id", "Age", "CustomerId", "Name");
    }
    foreach (var (name, fields) in new[] { ("items-a.db", "CreatedAt,Phone,Status"), ("items-b.db", "LastLogin,Score,CustomerId,Country,Status") })
    {
        var path = Put(Fresh(), "DropIndex_5_0_21", name);
        var names = fields.Split(',');
        BsonDocument Doc(int id) => new(names.ToDictionary(x => x, x => (BsonValue)(x + "x" + id))) { ["_id"] = id };
        using (var db = new LiteDatabase(path))
        {
            var col = db.GetCollection("items");
            RequireIndexes(db, "items", name, "_id", "Status"); // the only index 5.0.21 kept
            Write(() => col.Insert(Doc(1000)), $"{name}: insert");
            Require(col.Update(Doc(1)) && col.Delete(2), $"{name}: the update of _id 1 or the delete of _id 2 found no document");
            col.EnsureIndex(names[0]);
            IndexesMatchScan(db, "items", name, "_id", "Status", names[0]);
        }
        using (var db = new LiteDatabase(path))
        {
            var col = db.GetCollection("items");
            Require(col.Count() == 50 && col.Count(Query.EQ(names[0], names[0] + "x1")) == 1,
                $"{name} reopened: {col.Count()} documents, {col.Count(Query.EQ(names[0], names[0] + "x1"))} with {names[0]} = {names[0]}x1 (expected 50 and 1)");
            IndexesMatchScan(db, "items", name + " reopened", "_id", "Status", names[0]);
        }
    }
}

// Every index: the planner uses it, and each key's documents are those a full scan finds.
void IndexesMatchScan(LiteDatabase db, string collection, string stage, params string[] expected)
{
    var col = db.GetCollection(collection);
    RequireIndexes(db, collection, stage, expected);
    var docs = col.FindAll().ToList();
    foreach (var index in expected)
    {
        foreach (var key in docs.Select(x => x[index]).Distinct())
            RequireSame(Ids(col.Find(Query.EQ("$." + index, key))), Ids(docs.Where(x => x[index] == key)), $"{stage}: index {index} = {key}");
        RequirePlan(col, "$." + index, docs[0][index], index, stage);
    }
}

void ForeignWal()
{
    var failed = new List<string>();
    foreach (var options in new[] { "", ";ReadOnly=true;Legacy Index Scan=true", ";Auto-Rebuild=true" })
    {
        var directory = Fresh();
        var data = Put(directory, "WalCrash_5_0_21", "crash.db");
        Put(directory, "ForeignWal_5_0_21", "foreign-log.db", "crash-log.db");
        var before = Snapshot(directory);
        var seen = "";
        var error = Failure(() =>
        {
            using var db = new LiteDatabase($"Filename={data}{options}");
            seen = $"collections [{string.Join(",", db.GetCollectionNames().OrderBy(x => x))}], docs {db.GetCollection("docs").Count()}";
        });
        var label = options == "" ? "default open" : options.Substring(1);
        if (!(error is LiteException { ErrorCode: LiteException.INVALID_DATABASE } && error.Message.Contains("commits the header of another database")))
            failed.Add($"{label}: expected INVALID_DATABASE ({LiteException.INVALID_DATABASE}), \"commits the header of another database\"; " +
                $"got {Describe(error, seen)} ({Sizes(Snapshot(directory))})");
        else if (Failure(() => RequireUnchanged(directory, before, label)) is { } changed) failed.Add(changed.Message);
    }
    Require(failed.Count == 0, string.Join("\n     ", failed));
}

// 5.0.21 reopens this pair with a = 10 and b = 13 (3 of them, _id >= 100, only in the WAL).
void ConcurrentWalCrash()
{
    foreach (var options in new[] { "", ";ReadOnly=true;Legacy Index Scan=true" })
    {
        var directory = Fresh();
        var data = Put(directory, "ConcurrentWalCrash_5_0_21", "c.db", log: "c-log.db");
        var before = Snapshot(directory);
        for (var open = 1; open <= (options == "" ? 2 : 1); open++)
        {
            using var db = new LiteDatabase($"Filename={data}{options}");
            var (a, b, wal) = (db.GetCollection("a").Count(), db.GetCollection("b").Count(), db.GetCollection("b").Count(Query.GTE("_id", 100)));
            Require((a, b, wal) == (10, 13, 3),
                $"{(options == "" ? "writable open " + open : "read-only legacy scan")}: a={a}, b={b}, b with _id >= 100: {wal} (expected 10, 13, 3)");
        }
        if (options != "") RequireUnchanged(directory, before, "the read-only legacy scan");
    }
}

// damaged.db: c {_id: 1..3, a: "keep-i", b: "tail-i-zzz..."}, the length of document 2's "b" overwritten.
void DamagedDocumentRefused()
{
    var path = Put(Fresh(), "DamagedDocument_5_0_21", "damaged.db");
    var original = File.ReadAllBytes(path);
    var error = Failure(() => new LiteDatabase(path).Dispose());
    Require(error is LiteException { ErrorCode: LiteException.INVALID_DATAFILE_STATE } && error.Message.Contains("Collection 'c'") &&
        error.Message.Contains("auto-rebuild=true") && error.Message.Contains("legacy index scan=true"),
        $"default open: expected INVALID_DATAFILE_STATE ({LiteException.INVALID_DATAFILE_STATE}) naming collection 'c' and the remedies; got {Describe(error, "")}");
    var after = File.ReadAllBytes(path);
    Require(after[191] == 1, "default open: the rebuild mark (header byte 191) was not set");
    after[191] = original[191];
    Require(after.SequenceEqual(original) && Directory.GetFileSystemEntries(Path.GetDirectoryName(path)).Length == 1,
        "default open: the refused migration changed more than the rebuild mark");
    using var db = new LiteDatabase($"Filename={path};Auto-Rebuild=true");
    CheckSalvaged(db, "auto-rebuild after the default open");
}

void DamagedDocumentSalvaged()
{
    var path = Put(Fresh(), "DamagedDocument_5_0_21", "damaged.db");
    using (var db = new LiteDatabase($"Filename={path};Auto-Rebuild=true"))
    {
        CheckSalvaged(db, "auto-rebuild");
        db.GetCollection("c").Insert(new BsonDocument { ["_id"] = 4 });
    }
    Require(File.Exists(Path.Combine(Path.GetDirectoryName(path), "damaged-backup.db")), "auto-rebuild kept no damaged-backup.db");
    using var reopened = new LiteDatabase(path);
    Require(reopened.GetCollection("c").Count() == 4, $"reopened after auto-rebuild: {reopened.GetCollection("c").Count()} documents, expected 4");
}

void CheckSalvaged(LiteDatabase db, string stage)
{
    var col = db.GetCollection("c");
    var docs = col.FindAll().ToList();
    Require(Ids(docs).SequenceEqual(new[] { 1, 2, 3 }), $"{stage}: kept _id [{string.Join(",", Ids(docs))}], expected 1, 2, 3");
    foreach (var id in new[] { 1, 3 })
        Require(col.FindById(id)["a"] == "keep-" + id && col.FindById(id)["b"] == $"tail-{id}-" + new string('z', 20),
            $"{stage}: document {id} is {col.FindById(id)}");
    var partial = col.FindById(2);
    Require(partial.Keys.OrderBy(x => x).SequenceEqual(new[] { "_id", "a" }) && partial["a"] == "keep-2",
        $"{stage}: document 2 is {partial}, expected its readable part {{_id: 2, a: \"keep-2\"}}");
    var errors = db.GetCollection("_rebuild_errors").FindAll().Select(x => x["message"].AsString).ToArray();
    Require(errors.Any(x => x.Contains("Only the readable part of damaged document 2 was kept")),
        $"{stage}: _rebuild_errors [{string.Join(" | ", errors)}] does not name the partly kept document 2");
}

string Fresh() => Directory.CreateDirectory(Path.Combine(work, (++copies).ToString("D2"))).FullName;
// Copy an entry (and the given log entry beside it, as <name>-log.db) into the directory.
string Put(string directory, string archive, string entry, string name = null, string log = null)
{
    var target = Path.Combine(directory, name ?? entry);
    File.Copy(Path.Combine(fixtures, archive, entry), target);
    if (log != null) File.Copy(Path.Combine(fixtures, archive, log), Path.ChangeExtension(target, null) + "-log.db");
    return target;
}
string WalCrash(string directory) => Put(directory, "WalCrash_5_0_21", "crash.db", log: "crash-log.db");

static void RequirePlan(ILiteCollection<BsonDocument> col, string field, BsonValue key, string index, string stage)
{
    var plan = col.Query().Where(Query.EQ(field, key)).GetPlan()["index"];
    Require(plan["name"] == index, $"{stage}: {field} = {key} planned with index {plan["name"]}, expected {index}");
}
static void RequireIndexes(LiteDatabase db, string collection, string stage, params string[] expected)
{
    var names = db.GetCollection("$indexes").Find(Query.EQ("collection", collection)).Select(x => x["name"].AsString).ToArray();
    Require(names.OrderBy(x => x, StringComparer.Ordinal).SequenceEqual(expected.OrderBy(x => x, StringComparer.Ordinal)),
        $"{stage}: indexes [{string.Join(",", names)}], expected [{string.Join(",", expected)}]");
}
static void RequireSame(int[] found, int[] scan, string query) =>
    Require(found.SequenceEqual(scan), $"{query} returned _id [{string.Join(",", found)}], a full scan [{string.Join(",", scan)}]");
static int[] Ids(IEnumerable<BsonDocument> docs) => docs.Select(x => x["_id"].AsInt32).OrderBy(x => x).ToArray();
static void Write(Action write, string stage) { if (Failure(write) is { } error) throw new Exception($"{stage} failed: {Describe(error, "")}"); }
static Exception Failure(Action action) { try { action(); return null; } catch (Exception ex) { return ex; } }
static string Describe(Exception error, string seen) => error == null ? $"the open succeeded ({seen})"
    : $"{error.GetType().Name}{(error is LiteException lite ? $" {lite.ErrorCode}" : "")}: {error.Message}";
static Dictionary<string, byte[]> Snapshot(string directory) => Directory.GetFileSystemEntries(directory)
    .ToDictionary(Path.GetFileName, x => File.Exists(x) ? File.ReadAllBytes(x) : Array.Empty<byte>());
static string Sizes(Dictionary<string, byte[]> files) => string.Join(", ", files.OrderBy(x => x.Key).Select(x => $"{x.Key} {x.Value.Length} B"));
static void RequireUnchanged(string directory, Dictionary<string, byte[]> before, string stage)
{
    var after = Snapshot(directory);
    Require(after.Count == before.Count && before.All(x => after.TryGetValue(x.Key, out var bytes) && bytes.SequenceEqual(x.Value)),
        $"{stage} changed the files: before {Sizes(before)}; after {Sizes(after)}");
}
static void Require(bool condition, string message) { if (!condition) throw new Exception(message); }
