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
            if (args[0].StartsWith("compact-", StringComparison.Ordinal))
            {
                CompactCompatibility.Run(args);
                return;
            }
            foreach (var encrypted in new[] { false, true })
            {
                var suffix = encrypted ? "encrypted.db" : "plain.db";
                var password = encrypted ? "compatibility-test" : null;
                if (args[0] == "reclaim")
                {
                    Refuse(Path.Combine(args[1], "reclaimed-" + suffix), password);
                }
                else if (args[0] == "resume")
                {
                    using var legacy = new LiteDatabase(new ConnectionString
                    {
                        Filename = Path.Combine(args[1], "interrupted-" + suffix), Password = password
                    });
                    legacy.CheckpointSize = 0;
                    var docs = legacy.GetCollection("docs");
                    if (docs.FindById(1)["value"].AsString != "legacy") throw new Exception("Interrupted conversion lost data");
                    docs.Insert(new BsonDocument { ["_id"] = 2, ["value"] = "resumed" });
                    var sourceFile = Path.Combine(args[1], "interrupted-" + suffix);
                    var copy = Path.Combine(args[1], "interrupted-wal-" + suffix);
                    File.Copy(sourceFile, copy);
                    File.Copy(Path.ChangeExtension(sourceFile, null) + "-log.db", Path.ChangeExtension(copy, null) + "-log.db");
                    legacy.Checkpoint();
                }
                else if (args[0] == "create")
                {
                    Refuse(Path.Combine(args[1], "current-" + suffix), password);
                    using var legacy = new LiteDatabase(new ConnectionString
                    {
                        Filename = Path.Combine(args[1], "legacy-" + suffix), Password = password
                    });
                    legacy.GetCollection("docs").Insert(new BsonDocument { ["_id"] = 1, ["value"] = "legacy" });
                }
                else Refuse(Path.Combine(args[1], "legacy-" + suffix), password);
            }
            Console.WriteLine("LiteDB 5.0.21: " + args[0] + " passed (plain and encrypted)");
        }

        private static void Refuse(string file, string password)
        {
            var original = File.ReadAllBytes(file);
            var log = Path.ChangeExtension(file, null) + "-log" + Path.GetExtension(file);
            var originalLog = File.Exists(log) ? File.ReadAllBytes(log) : null;
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
                if (!rejected || !original.SequenceEqual(File.ReadAllBytes(file)) ||
                    (originalLog != null && (!File.Exists(log) || !originalLog.SequenceEqual(File.ReadAllBytes(log)))))
                    throw new Exception("Legacy engine must reject the newer format without changing data or WAL: " + mode);
            }
        }
    }
}
