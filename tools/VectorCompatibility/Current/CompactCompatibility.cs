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
                var connection = new ConnectionString { Filename = path, Password = password, CompactStorage = true };
                if (args[0] == "compact-promote")
                {
                    var original = File.ReadAllBytes(path);
                    connection.ReadOnly = true;
                    using (var db = new LiteDatabase(connection))
                        if (db.GetCollection("docs").FindById(1)["value"] != "legacy") throw new Exception("Lost legacy document");
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
                    var options = new RebuildOptions { CompactStorage = false, Password = password };
                    db.Rebuild(options);
                    if (options.GetErrorReport().Any()) throw new Exception("Downgrade failed");
                }
            }
            Console.WriteLine("Current engine: " + args[0] + " passed (plain and encrypted)");
        }
    }
}
