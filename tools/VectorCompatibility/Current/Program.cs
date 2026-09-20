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
                var file = Path.Combine(args[1], (mode == "create" ? "current-" : "legacy-") + suffix);
                var original = File.Exists(file) ? File.ReadAllBytes(file) : null;
                using (var db = new LiteDatabase(new ConnectionString
                {
                    Filename = file, Password = password, Upgrade = true, ReadOnly = mode == "readonly"
                }))
                {
                    var docs = db.GetCollection("docs");
                    if (mode == "create")
                    {
                        docs.Insert(new BsonDocument { ["_id"] = 1, ["Embedding"] = new BsonVector(new[] { 1f, 0f }) });
                        docs.EnsureIndex("embedding_idx", "$.Embedding", new VectorIndexOptions(2));
                        db.Rebuild(new RebuildOptions { Password = password });
                        if (docs.Query().TopKNear("Embedding", new[] { 1f, 0f }, 1).ToArray().Length != 1)
                            throw new Exception("Vector rebuild lost data");
                    }
                    else
                    {
                        if (docs.FindById(1)["value"].AsString != "legacy") throw new Exception("Lost legacy document");
                        if (mode == "convert")
                            docs.Insert(new BsonDocument { ["_id"] = 2, ["Embedding"] = new BsonVector(new[] { 1f, 0f }) });
                        else if (mode == "verify" && (docs.Count() != 2 || !docs.FindById(2)["Embedding"].IsVector))
                            throw new Exception("Conversion lost data");
                    }
                }
                if (mode == "readonly" && !System.Linq.Enumerable.SequenceEqual(original, File.ReadAllBytes(file)))
                    throw new Exception("Read-only legacy opens must preserve the file");
                if (mode != "create" && File.Exists(Path.ChangeExtension(file, null) + "-backup.db"))
                    throw new Exception("Automatic conversion must not require rebuilding the database");
            }
            Console.WriteLine("Current engine: " + args[0] + " passed (plain and encrypted)");
        }
    }
}
