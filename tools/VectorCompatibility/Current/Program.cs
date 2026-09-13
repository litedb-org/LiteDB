using System;
using System.IO;
using LiteDB;
using LiteDB.Engine;
using LiteDB.Vector;

namespace VectorCompatibility.Current
{
    internal static class Program
    {
        private static void Main(string[] args)
        {
            foreach (var encrypted in new[] { false, true })
            {
                var suffix = encrypted ? "encrypted.db" : "plain.db";
                var password = encrypted ? "compatibility-test" : null;
                var mode = args[0];
                var file = Path.Combine(args[1], (mode == "create" ? "v9-" : "v8-") + suffix);
                using (var db = new LiteDatabase(new ConnectionString { Filename = file, Password = password, Upgrade = true }))
                {
                    var docs = db.GetCollection("docs");
                    if (mode == "create")
                    {
                        docs.Insert(new BsonDocument { ["_id"] = 1, ["Embedding"] = new BsonVector(new[] { 1f, 0f }) });
                        docs.EnsureIndex("embedding_idx", "$.Embedding", new VectorIndexOptions(2));
                        db.Rebuild(new RebuildOptions { Password = password });
                        var result = docs.Query().TopKNear("Embedding", new[] { 1f, 0f }, 1).ToArray();
                        if (result.Length != 1 || !result[0]["Embedding"].IsVector) throw new Exception("Vector rebuild lost data");
                    }
                    else
                    {
                        if (docs.FindById(1)["value"].AsString != "legacy") throw new Exception("Lost legacy document");
                        if (mode == "ordinary") docs.Insert(new BsonDocument { ["_id"] = 2, ["value"] = "current" });
                        else if (mode == "promote")
                        {
                            if (docs.Count() != 3) throw new Exception("Ordinary round trip lost documents");
                            docs.Insert(new BsonDocument { ["_id"] = 4, ["Embedding"] = new BsonVector(new[] { 1f, 0f }) });
                        }
                        else if (docs.Count() != 4 || !docs.FindById(4)["Embedding"].IsVector)
                        {
                            throw new Exception("Promotion lost data");
                        }
                    }
                }
                if (mode != "create" && File.Exists(Path.ChangeExtension(file, null) + "-backup.db"))
                {
                    throw new Exception("Ordinary opening and vector promotion must not rebuild the database");
                }
                if (mode == "create") continue;
                using var ordinary = new LiteDatabase(new ConnectionString
                {
                    Filename = Path.Combine(args[1], "current-v8-" + suffix), Password = password
                });
                if (mode == "ordinary") ordinary.GetCollection("docs").Insert(new BsonDocument { ["_id"] = 1, ["value"] = "current" });
                else if (mode == "promote") ordinary.GetCollection("empty").EnsureIndex("vector", "$.Embedding", new VectorIndexOptions(2));
                else if (ordinary.GetCollection("docs").Count() != 2) throw new Exception("Empty index promotion lost ordinary data");
            }
            Console.WriteLine("Current engine: " + args[0] + " passed (plain and encrypted)");
        }
    }
}
