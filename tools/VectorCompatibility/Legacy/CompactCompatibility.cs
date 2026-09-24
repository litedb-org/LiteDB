using System;
using System.IO;
using System.Linq;

using LiteDB;

namespace VectorCompatibility.Legacy
{
    internal static class CompactCompatibility
    {
        internal static void Run(string[] args)
        {
            foreach (var password in new[] { null, "compatibility-test" })
            {
                var path = Path.Combine(args[1], "compact-" + (password == null ? "plain.db" : "encrypted.db"));
                if (args[0] == "compact-create")
                {
                    using var db = new LiteDatabase(new ConnectionString { Filename = path, Password = password });
                    db.GetCollection("docs").Insert(new BsonDocument { ["_id"] = 1, ["value"] = "legacy" });
                }
                else if (args[0] == "compact-reject")
                {
                    foreach (var file in new[] { path, path + ".array" })
                    foreach (var shared in new[] { false, true })
                    foreach (var readOnly in new[] { false, true })
                    {
                        var before = File.ReadAllBytes(file);
                        var rejected = false;
                        try
                        {
                            using var db = new LiteDatabase(new ConnectionString
                            {
                                Filename = file, Password = password, ReadOnly = readOnly,
                                Connection = shared ? ConnectionType.Shared : ConnectionType.Direct
                            });
                            db.GetCollection("docs").FindAll().ToArray();
                        }
                        catch (LiteException ex) when (ex.ErrorCode == LiteException.INVALID_DATABASE) { rejected = true; }
                        if (!rejected || !before.SequenceEqual(File.ReadAllBytes(file))) throw new Exception("Old engine must reject checksummed formats without mutation");
                    }
                }
                else
                {
                    using var db = new LiteDatabase(new ConnectionString { Filename = path, Password = password });
                    if (db.GetCollection("docs").FindAll().Count() != 50 || db.GetCollection("docs").FindById(50)["RepeatedPropertyName0"] != 50)
                        throw new Exception("Old engine cannot read downgraded documents");
                }
            }
            Console.WriteLine("LiteDB 5.0.21: " + args[0] + " passed (plain and encrypted)");
        }
    }
}
