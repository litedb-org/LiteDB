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
                var connection = new ConnectionString
                {
                    Filename = Path.Combine(args[1], args[0] == "create" ? "v9-" + suffix : "v8-" + suffix),
                    Password = encrypted ? "compatibility-test" : null,
                    Upgrade = args[0] == "migrate"
                };
                using var db = new LiteDatabase(connection);
                var docs = db.GetCollection("docs");
                if (args[0] == "create")
                {
                    docs.Insert(new BsonDocument { ["_id"] = 1, ["Embedding"] = new BsonVector(new[] { 1f, 0f }) });
                    docs.EnsureIndex("embedding_idx", "$.Embedding", new VectorIndexOptions(2));
                    db.Rebuild(new RebuildOptions { Password = connection.Password });
                    var result = docs.Query().TopKNear("Embedding", new[] { 1f, 0f }, 1).ToArray();
                    if (result.Length != 1 || !result[0]["Embedding"].IsVector) throw new Exception("Vector rebuild lost data");
                }
                else if (docs.Count() != 1 || docs.FindById(1)["value"].AsString != "legacy")
                {
                    throw new Exception("Migration lost legacy data");
                }
            }
            Console.WriteLine("Current engine: " + args[0] + " passed (plain and encrypted)");
        }
    }
}
