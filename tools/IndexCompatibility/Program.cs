using System;
using System.IO;
using System.Linq;
using LiteDB;

internal static class Program
{
    private static void Main(string[] args)
    {
        if (args[0].StartsWith("measure-", StringComparison.Ordinal))
        {
            Measurements.Run(args);
            return;
        }
        Directory.CreateDirectory(args[1]);
        var comparison = System.Globalization.CultureInfo.GetCultureInfo("en-US").CompareInfo;
        Console.WriteLine($"Runtime: {System.Runtime.InteropServices.RuntimeInformation.OSDescription}; " +
            $"en-US sort version {comparison.Version.FullVersion}/{comparison.Version.SortId}");
        foreach (var password in new[] { null, "index-compatibility" })
        foreach (var ordinal in new[] { false, true })
        foreach (var wal in new[] { false, true })
        foreach (var collision in new[] { false, true })
        {
            var name = $"{password != null}-{ordinal}-{wal}-{collision}.db";
            var filename = Path.Combine(args[1], name);
            var settings = new ConnectionString
            {
                Filename = filename, Password = password,
                Collation = ordinal && !collision ? Collation.Binary : new Collation("en-US/IgnoreCase")
            };
#if LEGACY
            if (args[0] == "create") Create(settings, wal, collision, ordinal);
            else if (!collision) Refuse(settings);
#else
            if (collision) RejectCollision(settings);
            else Verify(settings, args[0] == "verify");
#endif
        }
        CapacityFixtures(args[0], args[1]);
        Console.WriteLine(args[0] + ": 16 plain/encrypted, binary/culture, data/WAL, success/collision fixtures passed");
    }

    private static void CapacityFixtures(string mode, string directory)
    {
        foreach (var password in new[] { null, "index-compatibility" })
        {
            var settings = new ConnectionString
            {
                Filename = Path.Combine(directory, "capacity-" + (password != null) + ".db"),
                Password = password, Collation = new Collation("en-US/IgnoreCase")
            };
#if LEGACY
            if (mode != "create") { Refuse(settings); continue; }
            using var db = new LiteDatabase(settings);
            var rows = db.GetCollection("rows");
            rows.Insert(Enumerable.Range(1, 2000).Select(i => new BsonDocument
            {
                ["_id"] = i, ["key"] = new string('A', 200) + i
            }));
            rows.EnsureIndex("computed", "LOWER($.key)", true);
            db.Checkpoint();
            db.LimitSize = new FileInfo(settings.Filename).Length;
#else
            if (mode == "migrate")
            {
                var before = File.ReadAllBytes(settings.Filename);
                try
                {
                    using var rejected = new LiteDatabase(settings);
                    throw new Exception("Expected capacity admission failure");
                }
                catch (LiteException ex) when (ex.Message.Contains("Index migration capacity exceeds LIMIT_SIZE")) { }
                if (!before.SequenceEqual(File.ReadAllBytes(settings.Filename)))
                    throw new Exception("Capacity rejection changed the legacy file");
                settings.IndexMigrationLimitSize = 8 * 1024 * 1024;
            }
            else settings.ReadOnly = true;
            using var db = new LiteDatabase(settings);
            var rows = db.GetCollection("rows");
            if (db.LimitSize != 8 * 1024 * 1024 || rows.Count() != 2000 ||
                rows.Count("LOWER($.key) = @0", new string('a', 200) + 2) != 1)
                throw new Exception("Capacity recovery did not preserve data/indexes/budget");
            if (!settings.ReadOnly) db.Checkpoint();
#endif
        }
        Console.WriteLine(mode + ": real legacy LIMIT_SIZE fixtures passed");
    }

#if LEGACY
    private static void Create(ConnectionString settings, bool wal, bool collision, bool secondaryCollision)
    {
        using var db = new LiteDatabase(settings);
        db.UserVersion = 123;
        if (wal) db.CheckpointSize = 0;
        var rows = db.GetCollection("rows");
        if (collision)
        {
            var lower = new BsonDocument { ["text"] = "a" };
            var upper = new BsonDocument { ["text"] = "A" };
            rows.Insert(new BsonDocument { ["_id"] = secondaryCollision ? new BsonValue(1) : lower, ["key"] = lower });
            rows.Insert(new BsonDocument { ["_id"] = secondaryCollision ? new BsonValue(2) : upper, ["key"] = upper });
            if (secondaryCollision) rows.EnsureIndex("key", true);
            return;
        }
        var ids = new[] { "000000001122334455667788", "7fffffff1122334455667788",
            "800000001122337fff667788", "800000001122338000667788", "ffffffff1122334455667788" };
        for (var i = 0; i < ids.Length; i++)
            rows.Insert(new BsonDocument
            {
                ["_id"] = new ObjectId(ids[i]),
                ["n"] = i == 0 ? new BsonValue(9007199254740993L) : new BsonValue(9007199254740992d + i * 2),
                ["d"] = i % 2 == 0 ? new BsonDocument { ["b"] = 10 - i, ["a"] = i } : new BsonDocument { ["a"] = i, ["b"] = 10 - i },
                ["nested"] = new BsonArray { i % 2 == 0 ? "Z" : "a", i },
                ["values"] = new BsonArray { 9007199254740993L, 9007199254740992d }
            });
        rows.EnsureIndex("n");
        rows.EnsureIndex("d");
        rows.EnsureIndex("nested");
        rows.EnsureIndex("values", "$.values[*]");
        rows.EnsureIndex("computed", "$.n = 9007199254740992.0");
        // Exercise external sorting and migration safepoints on a larger collection.
        var many = db.GetCollection("many");
        var cultureKeys = new[] { "a-b", "ab", "a'b", "a b", "co-op", "coop", "résumé", "resume", "a\u030a", "å", "æ", "ae" };
        many.Insert(Enumerable.Range(1, 5000).Select(i => new BsonDocument
        {
            ["_id"] = i, ["value"] = cultureKeys[i % cultureKeys.Length] + new string('x', 200) + i
        }));
        many.EnsureIndex("value", true);
    }

    private static void Refuse(ConnectionString settings)
    {
        var before = File.ReadAllBytes(settings.Filename);
        foreach (var readOnly in new[] { false, true })
        {
            settings.ReadOnly = readOnly;
            try
            {
                using var db = new LiteDatabase(settings);
                db.GetCollection("rows").Count();
                throw new Exception("5.0.21 accepted migrated indexes");
            }
            catch (LiteException ex) when (ex.ErrorCode == LiteException.INVALID_DATABASE) { }
        }
        if (!before.SequenceEqual(File.ReadAllBytes(settings.Filename))) throw new Exception("Old engine mutated v10");
    }
#else
    private static void Verify(ConnectionString settings, bool readOnly)
    {
        settings.ReadOnly = readOnly;
        using var db = new LiteDatabase(settings);
        if (db.UserVersion != 123) throw new Exception("Lost user version");
        var rows = db.GetCollection("rows");
        var all = rows.FindAll().ToArray();
        if (all.Length != 5) throw new Exception("Lost records");
        var collation = db.Collation;
        foreach (var field in new[] { "_id", "n", "d", "nested" })
        {
            var indexed = rows.Query().OrderBy("$." + field).ToArray().Select(x => x[field]).ToArray();
            var scanned = all.Select(x => x[field]).OrderBy(x => x, collation).ToArray();
            if (!indexed.SequenceEqual(scanned)) throw new Exception("Index/scan mismatch: " + field);
            foreach (var document in all)
            {
                var expected = all.Count(x => collation.Compare(x[field], document[field]) == 0);
                if (rows.Count(Query.Parameterized.EQ(field, document[field])) != expected) throw new Exception("Seek mismatch: " + field);
            }
        }
        foreach (var number in new BsonValue[] { 9007199254740993L, 9007199254740992d })
            if (rows.Count("$.values ANY = @0", number) != 5) throw new Exception("Multikey key was not regenerated");
        var expectedComputed = all.Count(x => collation.Compare(x["n"], 9007199254740992d) == 0);
        if (rows.Count("($.n = 9007199254740992.0) = true") != expectedComputed) throw new Exception("Computed key was not regenerated");
        var many = db.GetCollection("many");
        var manyScan = many.FindAll().ToArray();
        var manyIndex = many.Query().OrderBy("$.value").ToArray().Select(x => x["value"]);
        if (manyScan.Length != 5000 || !manyIndex.SequenceEqual(manyScan.Select(x => x["value"]).OrderBy(x => x, collation)))
            throw new Exception("Large migration lost records");
        foreach (var document in manyScan.Take(24))
            if (many.Count(Query.Parameterized.EQ("value", document["value"])) != 1)
                throw new Exception("Cross-runtime string seek failed");
        if (!readOnly)
        {
            var first = all[0];
            rows.Update(first);
            db.Checkpoint();
        }
    }

    private static void RejectCollision(ConnectionString settings)
    {
        var data = File.ReadAllBytes(settings.Filename);
        var logFile = Path.Combine(Path.GetDirectoryName(settings.Filename), Path.GetFileNameWithoutExtension(settings.Filename) + "-log.db");
        var log = File.Exists(logFile) ? File.ReadAllBytes(logFile) : null;
        try
        {
            using var db = new LiteDatabase(settings);
            throw new Exception("Expected new canonical document identity collision");
        }
        catch (LiteException ex) when (ex.ErrorCode == LiteException.INDEX_DUPLICATE_KEY) { }
        if (!data.SequenceEqual(File.ReadAllBytes(settings.Filename))) throw new Exception("Collision mutated source data");
        if (log != null && !log.SequenceEqual(File.ReadAllBytes(logFile))) throw new Exception("Collision mutated source WAL");
    }
#endif
}
