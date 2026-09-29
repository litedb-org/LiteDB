using System;
using System.IO;
using System.Linq;
using System.Text;
using LiteDB;

/// <summary>
/// damaged.db files: written and closed by the released package, then byte-patched so that one
/// value inside a stored document is unreadable (its BSON length becomes 0x7FFFFFF0). 5.0.21 does
/// not write such bytes itself; the patches stand in for media or software damage.
/// </summary>
internal static class DamagedFiles
{
    private const int Damage = 0x7FFFFFF0;

    /// <summary>Collection "c", {_id: 1..3, a: "keep-i", b: "tail-i-" + 20 z}; the length of "b" of document 2 is damaged.</summary>
    public static void DamagedDocument(string output)
    {
        var path = Path.Combine(output, "damaged.db");
        using (var db = new LiteDatabase(path))
        {
            var col = db.GetCollection("c");
            for (var i = 1; i <= 3; i++) col.Insert(new BsonDocument { ["_id"] = i, ["a"] = "keep-" + i, ["b"] = "tail-" + i + "-" + new string('z', 20) });
        }
        // corrupt the int32 length of string field "b" of document 2 (its first occurrence)
        var bytes = File.ReadAllBytes(path);
        var marker = Encoding.UTF8.GetBytes("tail-2-");
        var p = Program.FindAll(bytes, marker).First();
        if (!bytes.AsSpan(p - 7, 3).SequenceEqual(new byte[] { 0x02, (byte)'b', 0 })) throw new InvalidOperationException("not field b");
        Program.WriteInt32(bytes, p - 4, Damage);
        Console.WriteLine("corrupted length at " + (p - 4));
        File.WriteAllBytes(path, bytes);
        ReadBack(path, "c", new[] { 1, 2, 3 });
    }

    /// <summary>
    /// Collection "c", unique index "b", {_id: 1..4, a: "keep-i", b: "u-i"}, checkpointed; the
    /// length of "a" of documents 2 and 3 is damaged, so only their _id is readable. The damaged
    /// file was then opened and read by 5.0.21 once (FindById 1, 4 and 2) before it was kept.
    /// </summary>
    public static void DamagedUniqueDocuments(string output)
    {
        var name = Path.Combine(output, "damaged.db");
        using (var db = new LiteDatabase(name))
        {
            var c = db.GetCollection("c");
            c.EnsureIndex("b", true);
            c.Insert(Enumerable.Range(1, 4).Select(i => new BsonDocument { ["_id"] = i, ["a"] = "keep-" + i, ["b"] = "u-" + i }));
            db.Checkpoint();
        }
        // damage the BSON length of string field "a" in documents 2 and 3
        var bytes = File.ReadAllBytes(name);
        foreach (var i in new[] { 2, 3 })
            Program.WriteInt32(bytes, Program.FindUnique(bytes, Program.BsonString("a", "keep-" + i)) + 3, Damage);
        File.WriteAllBytes(name, bytes);
        // 5.0.21 opens it and reads the undamaged documents
        using (var db = new LiteDatabase(name))
        {
            Console.WriteLine("5.0.21 FindById(1): " + (object)db.GetCollection("c").FindById(1));
            Console.WriteLine("5.0.21 FindById(4): " + (object)db.GetCollection("c").FindById(4));
            try { Console.WriteLine("5.0.21 FindById(2): " + (object)db.GetCollection("c").FindById(2)); }
            catch (Exception ex) { Console.WriteLine("5.0.21 FindById(2) fails: " + ex.Message); }
        }
        // 5.0.21's own rebuild of a copy fails on the unique index (the fixture is not rebuilt)
        OnCopy(name, copy =>
        {
            using var db = new LiteDatabase(copy);
            try { db.Rebuild(); Console.WriteLine("5.0.21 rebuild ok: " + db.GetCollection("c").Count() + " errors=" + db.GetCollection("_rebuild_errors").Count()); }
            catch (Exception ex) { Console.WriteLine("5.0.21 rebuild fails: " + ex.GetType().Name + ": " + ex.Message); }
        });
    }

    /// <summary>
    /// Collections "maxed", "mined", "long" and "throws", each {_id: 1..3, a: "keep-i", b: "u-i"}
    /// and an expression index "k"; checkpointed; the length of "b" of document 2 is damaged in
    /// every collection. The damaged file was then opened and read by 5.0.21 once before it was kept.
    /// </summary>
    public static void DamagedIndexKeys(string output)
    {
        var name = Path.Combine(output, "damaged.db");
        var indexes = new (string collection, string expression)[]
        {
            ("maxed", "COALESCE($.b, MAXVALUE())"),
            ("mined", "COALESCE($.b, MINVALUE())"),
            ("long", "COALESCE($.b, '" + new string('k', 1100) + "')"),
            ("throws", "SUBSTRING(COALESCE($.b, 'x'), 1, 2)"),
        };
        using (var db = new LiteDatabase(name))
        {
            foreach (var (collection, expression) in indexes)
            {
                var c = db.GetCollection(collection);
                c.Insert(Enumerable.Range(1, 3).Select(i => new BsonDocument { ["_id"] = i, ["a"] = "keep-" + i, ["b"] = "u-" + i }));
                c.EnsureIndex("k", expression);
                Console.WriteLine(collection + " keys: " + string.Join(",", c.Query().Select(BsonExpression.Create(expression)).ToArray()
                    .Select(x => x.ToString().Substring(0, Math.Min(20, x.ToString().Length)))));
            }
            db.Checkpoint();
        }
        // damage the BSON length of string field "b" of document 2 in every collection
        var bytes = File.ReadAllBytes(name);
        var hits = Program.FindAll(bytes, Program.BsonString("b", "u-2"));
        Console.WriteLine("damaged: " + hits.Length);
        if (hits.Length != indexes.Length) throw new InvalidOperationException("expected one document 2 per collection");
        foreach (var at in hits) Program.WriteInt32(bytes, at + 3, Damage);
        File.WriteAllBytes(name, bytes);
        using (var db = new LiteDatabase(name))
            foreach (var (collection, _) in indexes)
                Console.WriteLine($"5.0.21 {collection}: doc1={db.GetCollection(collection).FindById(1) != null} doc3={db.GetCollection(collection).FindById(3) != null}");
    }

    /// <summary>
    /// Collection "c", unique index "b" on $.b, {_id: 1, a: "keep-1", b: "u-1"}, {_id: 7, b: "u-7",
    /// a: "tail-7"}, {_id: 8, a: "keep-8", b: "u-8"}, {_id: 9, a: "keep-9", b: "u-9"}. Then, in the
    /// stored documents only (not in the index nodes): "b" of document 7 becomes "u-1", the _id of
    /// document 8 becomes 7, and the length of "a" of both is damaged. The originals made these
    /// patches with a Python script after the 5.0.21 process exited; they are ported here as is.
    /// </summary>
    public static void DamagedSalvageDuplicate(string output)
    {
        var path = Path.Combine(output, "damaged.db");
        using (var db = new LiteDatabase($"Filename={path};Connection=direct"))
        {
            var c = db.GetCollection("c");
            c.EnsureIndex("b", "$.b", unique: true);
            c.Insert(new BsonDocument { ["_id"] = 1, ["a"] = "keep-1", ["b"] = "u-1" });
            c.Insert(new BsonDocument { ["_id"] = 7, ["b"] = "u-7", ["a"] = "tail-7" });
            c.Insert(new BsonDocument { ["_id"] = 8, ["a"] = "keep-8", ["b"] = "u-8" });
            c.Insert(new BsonDocument { ["_id"] = 9, ["a"] = "keep-9", ["b"] = "u-9" });
        }
        using (var db = new LiteDatabase($"Filename={path};Connection=direct;ReadOnly=true"))
            Console.WriteLine("5.0.21 wrote: " + string.Join(" ", db.GetCollection("c").FindAll().Select(x => x.ToString())));

        var d = File.ReadAllBytes(path);
        // doc 7: b "u-7" -> "u-1", damage a's length
        var i = Program.FindUnique(d, Program.BsonString("b", "u-7"));
        Encoding.UTF8.GetBytes("u-1").CopyTo(d, i + 7);
        var j = Program.FindUnique(d, Program.BsonString("a", "tail-7"));
        Program.WriteInt32(d, j + 3, Damage);
        // doc 8: _id 8 -> 7, damage a's length
        var k = Program.FindUnique(d, Program.BsonInt32("_id", 8).Concat(Program.BsonString("a", "keep-8")).ToArray());
        Program.WriteInt32(d, k + 5, 7);
        Program.WriteInt32(d, k + 12, Damage);
        File.WriteAllBytes(path, d);
        Console.WriteLine($"patched at {i} {j} {k}");
        ReadBack(path, "c", new[] { 1, 7, 8, 9 });
    }

    /// <summary>What 5.0.21 reads from each document now (on a copy).</summary>
    private static void ReadBack(string path, string collection, int[] ids)
    {
        OnCopy(path, copy =>
        {
            using var db = new LiteDatabase($"Filename={copy};ReadOnly=true");
            foreach (var id in ids)
            {
                try { Console.WriteLine($"5.0.21 FindById({id}): {(object)db.GetCollection(collection).FindById(id) ?? "(none)"}"); }
                catch (Exception ex) { Console.WriteLine($"5.0.21 FindById({id}) fails: {ex.GetType().Name}: {ex.Message}"); }
            }
        });
    }

    private static void OnCopy(string path, Action<string> action)
    {
        var work = Program.WorkDirectory();
        try
        {
            var copy = Path.Combine(work, Path.GetFileName(path));
            File.Copy(path, copy);
            action(copy);
        }
        finally { Program.DeleteWorkDirectory(work); }
    }
}
