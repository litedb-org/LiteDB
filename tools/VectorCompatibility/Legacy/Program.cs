using System;
using System.IO;
using System.Linq;
using LiteDB;

namespace VectorCompatibility.Legacy
{
    internal static class Program
    {
        private static void Main(string[] args)
        {
            foreach (var encrypted in new[] { false, true })
            {
                var suffix = encrypted ? "encrypted.db" : "plain.db";
                var password = encrypted ? "compatibility-test" : null;
                if (args[0] == "create")
                {
                    Refuse(Path.Combine(args[1], "v9-" + suffix), password);
                    using var legacy = new LiteDatabase(new ConnectionString
                    {
                        Filename = Path.Combine(args[1], "v8-" + suffix), Password = password
                    });
                    legacy.GetCollection("docs").Insert(new BsonDocument { ["_id"] = 1, ["value"] = "legacy" });
                }
                else if (args[0] == "ordinary")
                {
                    foreach (var prefix in new[] { "v8-", "current-v8-" })
                    {
                        using var db = new LiteDatabase(new ConnectionString
                        {
                            Filename = Path.Combine(args[1], prefix + suffix), Password = password
                        });
                        var docs = db.GetCollection("docs");
                        var count = prefix == "v8-" ? 2 : 1;
                        if (docs.Count() != count || docs.FindById(count)["value"].AsString != "current")
                        {
                            throw new Exception("Current engine changed ordinary v8 compatibility");
                        }
                        docs.Insert(new BsonDocument { ["_id"] = count + 1, ["value"] = "legacy-again" });
                    }
                }
                else
                {
                    Refuse(Path.Combine(args[1], "v8-" + suffix), password);
                    Refuse(Path.Combine(args[1], "current-v8-" + suffix), password);
                }
            }
            Console.WriteLine("LiteDB 5.0.21: " + args[0] + " passed (plain and encrypted)");
        }

        private static void Refuse(string file, string password)
        {
            var original = File.ReadAllBytes(file);
            foreach (var mode in new[] { "read", "write", "rebuild", "upgrade" })
            {
                var rejected = false;
                try
                {
                    using var db = new LiteDatabase(new ConnectionString
                    {
                        Filename = file, Password = password, ReadOnly = mode == "read", Upgrade = mode == "upgrade"
                    });
                    if (mode == "rebuild") db.Rebuild();
                    else if (mode == "write") db.GetCollection("docs").Insert(new BsonDocument { ["_id"] = 99 });
                    else db.GetCollection("docs").Count();
                }
                catch (LiteException ex) when (ex.ErrorCode == LiteException.INVALID_DATABASE)
                {
                    rejected = true;
                }
                if (!rejected || !original.SequenceEqual(File.ReadAllBytes(file)))
                {
                    throw new Exception("Legacy engine must reject v9 without changing the file: " + mode);
                }
            }
        }
    }
}
