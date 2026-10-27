using System;
using System.IO;
using System.Linq;
using LiteDB;

// Built against a pinned pre-worker v6 library by the hosted compatibility probe.
internal static class Program
{
    private static void Main(string[] args)
    {
        foreach (var encrypted in new[] { false, true })
        foreach (var dirty in new[] { false, true })
        {
            var suffix = (dirty ? "dirty-" : "clean-") + (encrypted ? "encrypted.db" : "plain.db");
            var source = Path.Combine(args[0], "source-before-" + suffix);
            using var db = new LiteDatabase(new ConnectionString
            {
                Filename = source, Password = encrypted ? "compatibility-test" : null, DurableCommits = false
            });
            db.CheckpointSize = 0;
            db.GetCollection("rows").Insert(Enumerable.Range(0, 48).Select(Row));
            db.GetCollection("rows").EnsureIndex("value", true);
            db.GetCollection("sentinel").Insert(new BsonDocument { ["_id"] = 1, ["payload"] = "untouched" });
            db.CheckpointSize = 8;
            db.Checkpoint();
            if (dirty)
            {
                db.CheckpointSize = 0;
                for (var version = 1; version <= 600; version++) db.UserVersion = version;
                // Publish positive N without allowing automatic work before copying.
                // An explicit transaction's WAL header retains CP0; the verifier
                // lowers N after recovery to establish admission from the large tail.
            }
            var target = Path.Combine(args[0], "before-" + suffix);
            File.Copy(source, target);
            if (File.Exists(LogName(source))) File.Copy(LogName(source), LogName(target));
            if (dirty && new FileInfo(LogName(target)).Length < 600L * 8192)
                throw new Exception("Pre-change producer did not create a large acknowledged WAL");
        }
        Console.WriteLine("Pinned pre-worker v6 clean and large dirty inputs created");
    }

    private static BsonDocument Row(int id) => new BsonDocument
    {
        ["_id"] = id, ["value"] = id * 7, ["payload"] = new string((char)('a' + id % 26), 1000)
    };
    private static string LogName(string file) => Path.ChangeExtension(file, null) + "-log.db";
}
