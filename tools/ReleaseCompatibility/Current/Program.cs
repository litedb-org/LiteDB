using System;
using System.IO;
using System.Linq;
using LiteDB;

internal static class Program
{
    private static void Main(string[] args)
    {
        if (args[0] == "damaged") { DamagedFile.Verify(args[1]); return; }
        var settings = new ConnectionString
        {
            Filename = args[0], Password = args[1] == "encrypted" ? Corpus.Password : null,
            Connection = ConnectionType.Direct, ReadOnly = true, LegacyIndexScan = true
        };
        var before = File.ReadAllBytes(settings.Filename);
        if (args[2].StartsWith("4.", StringComparison.Ordinal))
        {
            // v7 has an explicit format boundary. Read-only rejection must preserve
            // the old file; Upgrade=true is tested separately on the writable copy.
            try
            {
                using var refused = new LiteDatabase(settings);
                throw new Exception("v4 was accepted without Upgrade=true");
            }
            catch (LiteException ex) when (ex.ErrorCode == LiteException.INVALID_DATABASE ||
                ex.ErrorCode == LiteException.UNSUPPORTED_FILE_VERSION ||
                (args[1] == "encrypted" && ex.ErrorCode == LiteException.NOT_ENCRYPTED)) { }
        }
        else
        {
            using var db = new LiteDatabase(settings);
            Verify(db, false);
        }
        if (!before.SequenceEqual(File.ReadAllBytes(settings.Filename))) throw new Exception("Read-only open mutated original");

        settings.ReadOnly = false;
        settings.LegacyIndexScan = false;
        settings.Upgrade = args[2].StartsWith("4.", StringComparison.Ordinal);
        using (var db = new LiteDatabase(settings))
        {
            Verify(db, false);
            foreach (var name in Corpus.Collections)
                db.GetCollection(name).Insert(Corpus.Document(name, Corpus.Count));
            db.BeginTrans();
            db.GetCollection("integers").Insert(Corpus.Document("integers", Corpus.Count + 1));
            db.Rollback();
        }
        settings.Upgrade = false;
        for (var reopen = 0; reopen < 2; reopen++)
        {
            settings.ReadOnly = reopen == 1;
            using var db = new LiteDatabase(settings);
            Verify(db, true);
        }
        Console.WriteLine("PASS " + args[2] + " " + args[1] + ": payloads, indexes, direct writes, rollback, repeated reopen");
    }

    private static void Verify(LiteDatabase db, bool appended)
    {
        var count = Corpus.Count + (appended ? 1 : 0);
        if (!db.GetCollectionNames().OrderBy(x => x).SequenceEqual(Corpus.Collections.OrderBy(x => x)))
            throw new Exception("Unexpected collections (including possible upgrade errors)");
        foreach (var name in Corpus.Collections)
        {
            var rows = db.GetCollection(name);
            var all = rows.FindAll().ToArray();
            if (all.Length != count) throw new Exception("Lost/duplicated records: " + name);
            var bySequence = all.ToDictionary(x => x["sequence"].AsInt32);
            for (var i = 0; i < count; i++)
            {
                var expected = Corpus.Document(name, i);
                var actual = bySequence[i];
                if (actual == null || !BsonSerializer.Serialize(actual).SequenceEqual(BsonSerializer.Serialize(expected)))
                    throw new Exception("Payload mismatch: " + name + "/" + i);
                if (i % 16 != 0 && i != count - 1) continue;
                if (!BsonSerializer.Serialize(rows.FindById(expected["_id"])).SequenceEqual(BsonSerializer.Serialize(expected)))
                    throw new Exception("Primary-key seek mismatch");
                foreach (var field in new[] { "sequence", "code", "text" })
                {
                    var indexed = rows.Find(Query.EQ(field, expected[field])).Select(x => x["sequence"].AsInt32).OrderBy(x => x);
                    var scanned = all.Where(x => db.Collation.Compare(x[field], expected[field]) == 0).Select(x => x["sequence"].AsInt32).OrderBy(x => x);
                    if (!indexed.SequenceEqual(scanned)) throw new Exception("Index mismatch: " + name + "/" + field);
                }
            }
            if (!rows.Query().OrderBy("$.sequence").ToArray().Select(x => x["sequence"].AsInt32).SequenceEqual(Enumerable.Range(0, count)))
                throw new Exception("Ordered index scan mismatch");
            if (rows.Count("$.tags ANY = @0", "all") != count) throw new Exception("Array membership mismatch");
            if (rows.Count("LOWER($.code) = @0", "code-000128") != 1) throw new Exception("Computed expression mismatch");
        }
    }
}
