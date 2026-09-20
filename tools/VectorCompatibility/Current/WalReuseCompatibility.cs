using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using LiteDB;
using LiteDB.Engine;

namespace VectorCompatibility.Current
{
    internal static class WalReuseCompatibility
    {
        internal static void Run(string directory, bool create)
        {
            foreach (var encrypted in new[] { false, true })
            {
                var password = encrypted ? "compatibility-test" : null;
                var file = Path.Combine(directory, "reclaimed-" + (encrypted ? "encrypted.db" : "plain.db"));
                if (!create)
                {
                    using var check = new LiteDatabase(new ConnectionString { Filename = file, Password = password });
                    if (check.GetCollection("cold").FindById(0)["value"].AsInt32 != 22 ||
                        check.GetCollection("cold").FindById(1)["value"].AsInt32 != 21 ||
                        check.GetCollection("victim").FindAll().Any(doc => doc["value"].AsInt32 != 0) ||
                        check.GetCollection("hot").FindById(0)["value"].AsInt32 != 20)
                        throw new Exception("Legacy checkpoint lost reclaimed WAL updates");
                    continue;
                }

                var source = Path.Combine(directory, "source-" + Path.GetFileName(file));
                using var engine = new LiteEngine(new EngineSettings
                {
                    Filename = source, Password = password, TransactionPageLimit = 1
                });
                using var database = new LiteDatabase(engine, disposeOnClose: false);
                database.Pragma(Pragmas.CHECKPOINT, 0);
                var cold = database.GetCollection("cold");
                var victim = database.GetCollection("victim");
                var hot = database.GetCollection("hot");
                cold.Insert(Documents(0));
                victim.Insert(Documents(0));
                hot.Insert(Documents(0));
                for (var value = 1; value <= 20; value++) hot.Update(Documents(value));
                using var reader = engine.Query("hot", new Query());
                var length = new FileInfo(LogName(source)).Length;
                engine.Checkpoint();
                Task.Run(() => cold.Update(Documents(21))).GetAwaiter().GetResult();
                if (new FileInfo(LogName(source)).Length > length + 3 * 8192)
                    throw new Exception("The compatibility fixture must actually reuse WAL slots");
                var beforeAbandoned = new FileInfo(LogName(source)).Length;
                Task.Run(() =>
                {
                    database.BeginTrans();
                    victim.Update(Documents(99));
                    database.Rollback();
                }).GetAwaiter().GetResult();
                var afterAbandoned = new FileInfo(LogName(source)).Length;
                if (afterAbandoned != beforeAbandoned + 8192)
                    throw new Exception("The abandoned transaction must reuse holes after its tail anchor: " +
                        (afterAbandoned - beforeAbandoned));
                // Preserve the reclaimed, reused WAL before reader disposal permits reset.
                File.Copy(source, file);
                File.Copy(LogName(source), LogName(file));
                while (reader.Read())
                    if (reader.Current["value"].AsInt32 != 20) throw new Exception("Old reader changed");
            }
            Console.WriteLine("Current engine: reclaimed WAL compatibility " + (create ? "created" : "verified"));
        }

        private static BsonDocument[] Documents(int value) => Enumerable.Range(0, 16).Select(id =>
            new BsonDocument { ["_id"] = id, ["value"] = value, ["payload"] = new string('x', 1500) }).ToArray();

        private static string LogName(string filename) =>
            Path.Combine(Path.GetDirectoryName(filename), Path.GetFileNameWithoutExtension(filename) + "-log" + Path.GetExtension(filename));
    }
}
