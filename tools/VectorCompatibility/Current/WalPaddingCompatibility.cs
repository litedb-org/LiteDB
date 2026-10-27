using System;
using System.IO;
using System.Linq;
using LiteDB;
using LiteDB.Engine;

namespace VectorCompatibility.Current
{
    internal static class WalPaddingCompatibility
    {
        internal static void Run(string directory, bool create)
        {
            foreach (var encrypted in new[] { false, true })
            {
                var password = encrypted ? "compatibility-test" : null;
                var suffix = encrypted ? "encrypted.db" : "plain.db";
                var counts = new[] { 1, 127, 128, 129 };
                if (!create)
                {
                    foreach (var count in counts)
                    {
                        var file = Path.Combine(directory, "padding-" + count + "-" + suffix);
                        for (var attempt = 0; attempt < 2; attempt++)
                        {
                            using var engine = new LiteEngine(new EngineSettings { Filename = file, Password = password });
                            using var db = new LiteDatabase(engine, disposeOnClose: false);
                            var docs = db.GetCollection("docs");
                            if (docs.Count() != count || Enumerable.Range(1, count).Any(id =>
                                docs.FindById(id)?["payload"].AsString != new string((char)('a' + id % 26), 100)))
                                throw new Exception("Padding compatibility recovery lost a document: " + file);
                            db.Checkpoint();
                        }
                    }
                    continue;
                }
                var source = Path.Combine(directory, "padding-source-" + suffix);
                using var writer = new LiteEngine(new EngineSettings { Filename = source, Password = password });
                using var database = new LiteDatabase(writer, disposeOnClose: false);
                database.CheckpointSize = 0;
                for (var id = 1; id <= counts.Max(); id++)
                {
                    database.GetCollection("docs").Insert(new BsonDocument
                    {
                        ["_id"] = id, ["payload"] = new string((char)('a' + id % 26), 100)
                    });
                    if (!counts.Contains(id)) continue;
                    var file = Path.Combine(directory, "padding-" + id + "-" + suffix);
                    File.Copy(source, file);
                    File.Copy(LogName(source), LogName(file));
                    if (new FileInfo(LogName(file)).Length % 8192 != 0)
                        throw new Exception("An acknowledged WAL must already have compatibility padding");
                }
            }
            Console.WriteLine("Combined WAL padding: " + (create ? "created" : "verified") + " (plain and encrypted)");
        }

        private static string LogName(string filename) => Path.Combine(Path.GetDirectoryName(filename),
            Path.GetFileNameWithoutExtension(filename) + "-log" + Path.GetExtension(filename));
    }
}
