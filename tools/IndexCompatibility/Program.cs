using System;
using System.IO;
using System.Linq;
using LiteDB;

internal static class Program
{
    private static void Main(string[] args)
    {
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
        Console.WriteLine(args[0] + ": 16 plain/encrypted, binary/culture, data/WAL, success/collision fixtures passed");
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
        many.Insert(Enumerable.Range(1, 5000).Select(i => new BsonDocument
        {
            ["_id"] = i, ["value"] = new string((char)('a' + i % 26), 200) + i
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
        if (many.Count() != 5000 || many.Query().OrderBy("$.value").ToArray().Length != 5000)
            throw new Exception("Large migration lost records");
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
