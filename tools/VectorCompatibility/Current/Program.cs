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
            if (args[0].StartsWith("compact-", StringComparison.Ordinal))
            {
                CompactCompatibility.Run(args);
                return;
            }
            foreach (var encrypted in new[] { false, true })
            {
                var suffix = encrypted ? "encrypted.db" : "plain.db";
                var password = encrypted ? "compatibility-test" : null;
                var mode = args[0];
                if (mode == "interrupt")
                {
                    InterruptedConversion.Create(args[1], suffix, password);
                    continue;
                }
                if (mode == "interrupt-unsealed" || mode == "resumed-unsealed")
                {
                    if (mode == "interrupt-unsealed") InterruptedConversion.CreateUnsealed(args[1], suffix, password);
                    else InterruptedConversion.VerifyUnsealed(args[1], suffix, password);
                    continue;
                }
                var prefix = mode == "create" ? "current-" : mode == "resumed" ? "interrupted-" : mode == "resumed-wal" ? "interrupted-wal-" : "legacy-";
                var file = Path.Combine(args[1], prefix + suffix);
                var original = File.Exists(file) ? File.ReadAllBytes(file) : null;
                if (mode == "readonly")
                {
                    try
                    {
                        using var rejected = new LiteDatabase(new ConnectionString
                            { Filename = file, Password = password, ReadOnly = true });
                        throw new Exception("Legacy read-only open must request index migration.");
                    }
                    catch (LiteException error) when (error.Message.Contains("index ordering/collation requires migration")) { }
                    if (!System.Linq.Enumerable.SequenceEqual(original, File.ReadAllBytes(file)))
                        throw new Exception("Rejected read-only migration changed data.");
                    continue;
                }
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
                        else if (mode.StartsWith("resumed") && (docs.Count() != 2 || docs.FindById(2)["value"].AsString != "resumed"))
                            throw new Exception("Resuming conversion lost a legacy write");
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
