using System;
using System.IO;
using System.Linq;
using System.Threading;
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
                    using var reopened = new LiteEngine(new EngineSettings { Filename = file, Password = password });
                    using var check = new LiteDatabase(reopened, disposeOnClose: false);
                    check.BeginTrans();
                    if (reopened.GetMonitor().GetThreadTransaction().TransactionID <= uint.Parse(File.ReadAllText(file + ".maxid")))
                        throw new Exception("Recovery reused an abandoned transaction identity");
                    check.Rollback();
                    if (check.GetCollection("cold").FindById(0)["value"].AsInt32 != 21 ||
                        check.GetCollection("cold").FindById(1)["value"].AsInt32 != 21 ||
                        check.GetCollection("victim").FindAll().Any(doc => doc["value"].AsInt32 != 0) ||
                        check.GetCollection("hot").FindById(0)["value"].AsInt32 != 20 ||
                        check.GetCollection("tail").FindAll().Any(doc => doc["value"].AsInt32 != 1))
                        throw new Exception("Recovery lost reclaimed WAL updates: " + string.Join("; ", new[] { "cold", "victim", "hot", "tail" }.Select(name => name + "=" + string.Join(",", check.GetCollection(name).FindAll().Select(doc => doc["value"].AsInt32)))));
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
                var tail = database.GetCollection("tail");
                cold.Insert(Documents(0));
                victim.Insert(Documents(0));
                hot.Insert(Documents(0));
                tail.Insert(Documents(0));
                for (var value = 1; value <= 20; value++) hot.Update(Documents(value));
                using var reader = engine.Query("hot", new Query());
                engine.Checkpoint();
                var length = new FileInfo(LogName(source)).Length;
                Task.Run(() => cold.Update(Documents(21))).GetAwaiter().GetResult();
                if (new FileInfo(LogName(source)).Length > length + 2 * Constants.PAGE_SIZE)
                    throw new Exception("The compatibility fixture must actually reuse WAL slots");
                using var lowAllocated = new ManualResetEventSlim();
                using var highWritten = new ManualResetEventSlim();
                uint provisionalLow = 0;
                uint abandonedHigh = 0;
                uint finalLow = 0;
                var beforeAbandoned = new FileInfo(LogName(source)).Length;
                long afterAbandoned = 0;
                var low = Task.Run(() =>
                {
                    database.BeginTrans();
                    var transaction = engine.GetMonitor().GetThreadTransaction();
                    provisionalLow = transaction.TransactionID;
                    lowAllocated.Set();
                    if (!highWritten.Wait(TimeSpan.FromSeconds(20)))
                        throw new TimeoutException("The higher-ID transaction did not write its WAL frames");
                    tail.Update(Documents(1));
                    database.Commit();
                    finalLow = transaction.TransactionID;
                });
                var high = Task.Run(() =>
                {
                    if (!lowAllocated.Wait(TimeSpan.FromSeconds(20)))
                        throw new TimeoutException("The lower-ID transaction was not allocated");
                    try
                    {
                        database.BeginTrans();
                        var transaction = engine.GetMonitor().GetThreadTransaction();
                        victim.Update(Documents(99));
                        abandonedHigh = transaction.TransactionID;
                        database.Rollback();
                        afterAbandoned = new FileInfo(LogName(source)).Length;
                    }
                    finally
                    {
                        highWritten.Set();
                    }
                });
                Task.WhenAll(low, high).GetAwaiter().GetResult();
                if (afterAbandoned != beforeAbandoned)
                    throw new Exception("The abandoned transaction did not reuse reclaimed WAL slots: " +
                        (afterAbandoned - beforeAbandoned));
                if (provisionalLow >= abandonedHigh || finalLow != provisionalLow)
                    throw new Exception($"The fixture did not preserve the delayed transaction identity: " +
                        $"{provisionalLow}, {abandonedHigh}, {finalLow}");

                File.WriteAllText(file + ".maxid", abandonedHigh.ToString());
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
