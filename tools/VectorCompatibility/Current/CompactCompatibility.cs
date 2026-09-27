using System;
using System.IO;
using System.Linq;

using LiteDB;
using LiteDB.Engine;

namespace VectorCompatibility.Current
{
    internal static class CompactCompatibility
    {
        internal static void Run(string[] args)
        {
            foreach (var password in new[] { null, "compatibility-test" })
            {
                var path = Path.Combine(args[1], "compact-" + (password == null ? "plain.db" : "encrypted.db"));
                var connection = new ConnectionString { Filename = path, Password = password, CompactStorage = CompactStorageMode.Compact };
                if (args[0] == "compact-promote")
                {
                    var original = File.ReadAllBytes(path);
                    connection.ReadOnly = true;
                    try
                    {
                        using var rejected = new LiteDatabase(connection);
                        throw new Exception("Legacy read-only open must request index migration.");
                    }
                    catch (LiteException error) when (error.Message.Contains("index ordering/collation requires migration")) { }
                    if (!original.SequenceEqual(File.ReadAllBytes(path))) throw new Exception("Read-only open mutated legacy file");
                    connection.ReadOnly = false;
                    using (var db = new LiteDatabase(connection))
                    {
                        db.GetCollection("docs").Insert(Enumerable.Range(2, 49).Select(i =>
                        {
                            var doc = new BsonDocument { ["_id"] = i };
                            for (var f = 0; f < 15; f++) doc["RepeatedPropertyName" + f] = i + f;
                            return doc;
                        }));
                    }
                    // An inline, array-only fixture exercises compact storage without a schema catalog.
                    connection.Filename = path + ".array";
                    using var array = new LiteDatabase(connection);
                    array.GetCollection("docs").Insert(new BsonDocument
                    {
                        ["_id"] = 1, ["values"] = new BsonArray(Enumerable.Range(0, 500).Select(i => new BsonValue(i)))
                    });
                }
                else
                {
                    using var db = new LiteDatabase(connection);
                    if (db.GetCollection("docs").FindAll().Count() != 50) throw new Exception("Mixed file lost documents");
                    var options = new RebuildOptions { CompactStorage = CompactStorageMode.Legacy, Password = password };
                    db.Rebuild(options);
                    if (options.GetErrorReport().Any()) throw new Exception("BSON rebuild failed");
                    if (db.GetCollection("docs").FindById(1)["value"] != "legacy") throw new Exception("Lost legacy document");
                    foreach (var doc in db.GetCollection("docs").FindAll().Where(doc => doc["_id"].AsInt32 > 1))
                    {
                        for (var f = 0; f < 15; f++)
                            if (doc["RepeatedPropertyName" + f] != doc["_id"].AsInt32 + f) throw new Exception("BSON rebuild changed payload");
                    }
                    // Inspect only after releasing engine handles, including on Windows.
                    db.Dispose();
                    VerifyBsonFile(path, password);
                }
            }
            Console.WriteLine("Current engine: " + args[0] + " passed (plain and encrypted)");
        }

        private static void VerifyBsonFile(string path, string password)
        {
            using var file = File.OpenRead(path);
            using var data = password == null ? (Stream)file : new AesStream(password, file, allowRecovery: false);
            var page = new byte[8192];
            for (long offset = 0; offset < data.Length; offset += page.Length)
            {
                data.ReadExactly(page);
                if (offset == 0 && page[59] != 11)
                    throw new Exception("BSON rebuild did not persist format v11");
                if (page[4] == 6)
                    throw new Exception("BSON rebuild retained a compact schema page at " + offset);
            }
        }
    }
}
