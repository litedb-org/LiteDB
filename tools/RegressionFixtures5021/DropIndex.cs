using System;
using System.IO;
using System.Linq;
using LiteDB;

/// <summary>
/// customers.db, items-a.db, items-b.db: collections from which 5.0.21 dropped indexes. Its
/// CollectionPage.UpdateBuffer leaves the tail of the old index list on the page. Written directly
/// by the released package, each file by a process of its own as originally; nothing is patched.
/// </summary>
internal static class DropIndex
{
    public static void Generate(string output)
    {
        foreach (var name in new[] { "customers", "items-a", "items-b" })
            Program.RunChild("drop-" + name, Path.Combine(output, name + ".db"));
        foreach (var name in new[] { "customers.db", "items-a.db", "items-b.db" }) Check(Path.Combine(output, name));
    }

    public static int Child(string mode, string path)
    {
        switch (mode)
        {
            case "drop-customers": Customers(path, "Age"); break;
            // Two of 40 random layouts (seeded) whose stale bytes failed every insert after migration.
            case "drop-items-a": Items(path, "CreatedAt,Phone,Status", "CreatedAt,Phone"); break;
            case "drop-items-b": Items(path, "LastLogin,Score,CustomerId,Country,Status", "CustomerId,LastLogin,Country,Score"); break;
            default: throw new ArgumentException("unknown child mode " + mode);
        }
        return 0;
    }

    /// <summary>Collection "customers", indexes Name, Age, CustomerId, 200 documents, then drop.</summary>
    private static void Customers(string path, string drop)
    {
        using (var db = new LiteDatabase(path))
        {
            var col = db.GetCollection("customers");
            col.EnsureIndex("Name");
            col.EnsureIndex("Age");
            col.EnsureIndex("CustomerId");
            col.InsertBulk(Enumerable.Range(1, 200).Select(i => new BsonDocument { ["_id"] = i, ["Name"] = "n" + i, ["Age"] = i % 90, ["CustomerId"] = "C" + i }));
            foreach (var d in drop.Split(',')) Console.WriteLine("drop " + d + " -> " + col.DropIndex(d));
            Console.WriteLine("indexes: " + string.Join(",", db.GetCollection("$indexes").Find("collection = 'customers'").Select(x => x["name"].AsString)));
        }
    }

    /// <summary>Collection "items", one index per field, 50 documents {_id: i, field: field + i}, then drop.</summary>
    private static void Items(string path, string idx, string drop)
    {
        using (var db = new LiteDatabase(path))
        {
            var col = db.GetCollection("items");
            var names = idx.Split(',');
            foreach (var n in names) col.EnsureIndex(n);
            col.InsertBulk(Enumerable.Range(1, 50).Select(i =>
            {
                var d = new BsonDocument { ["_id"] = i };
                foreach (var n in names) d[n] = n + i;
                return d;
            }));
            foreach (var d in drop.Split(',')) col.DropIndex(d);
        }
    }

    /// <summary>5.0.21 itself keeps using the file: insert, update, delete, EnsureIndex, reopen (on a copy).</summary>
    private static void Check(string path)
    {
        var work = Program.WorkDirectory();
        try
        {
            var copy = Path.Combine(work, "check.db");
            File.Copy(path, copy);
            string collection;
            using (var db = new LiteDatabase(copy))
            {
                collection = db.GetCollectionNames().Single();
                var col = db.GetCollection(collection);
                var indexes = db.GetCollection("$indexes").Find($"collection = '{collection}'").Select(x => x["name"].AsString).ToArray();
                var fields = indexes.Where(x => x != "_id").ToArray();
                Console.Write($"{Path.GetFileName(path)}: {collection} count={col.Count()} indexes={string.Join(",", indexes)}");
                var doc = new BsonDocument { ["_id"] = 1000 };
                foreach (var field in fields) doc[field] = field + "x";
                col.Insert(doc);
                doc["_id"] = 1;
                if (!col.Update(doc) || !col.Delete(2)) throw new InvalidOperationException("5.0.21 could not update or delete");
                col.EnsureIndex("zz");
            }
            using (var db = new LiteDatabase(copy)) Console.WriteLine($"; 5.0.21 writes and reopens: count={db.GetCollection(collection).Count()}");
        }
        finally { Program.DeleteWorkDirectory(work); }
    }
}
