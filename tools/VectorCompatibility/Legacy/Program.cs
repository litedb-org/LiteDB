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
                var file = Path.Combine(args[0], "v9-" + suffix);
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
                        else if (mode == "write") db.GetCollection("docs").Insert(new BsonDocument { ["_id"] = 2 });
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

                using var legacy = new LiteDatabase(new ConnectionString
                {
                    Filename = Path.Combine(args[0], "v8-" + suffix), Password = password
                });
                legacy.GetCollection("docs").Insert(new BsonDocument { ["_id"] = 1, ["value"] = "legacy" });
            }
            Console.WriteLine("LiteDB 5.0.21: refused all v9 operations and created migration fixtures");
        }
    }
}
