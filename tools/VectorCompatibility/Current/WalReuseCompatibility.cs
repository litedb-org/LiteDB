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
                    using var check = new LiteDatabase(new ConnectionString { Filename = file, Password = password });
                    if (check.GetCollection("cold").FindById(0)["value"].AsInt32 != 22 ||
                        check.GetCollection("cold").FindById(1)["value"].AsInt32 != 21 ||
                        check.GetCollection("victim").FindAll().Any(doc => doc["value"].AsInt32 != 0) ||
                        check.GetCollection("hot").FindById(0)["value"].AsInt32 != 20 ||
                        check.GetCollection("tail").FindAll().Any(doc => doc["value"].AsInt32 != 1))
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
                var tail = database.GetCollection("tail");
                cold.Insert(Documents(0));
                victim.Insert(Documents(0));
                hot.Insert(Documents(0));
                tail.Insert(Documents(0));
                for (var value = 1; value <= 20; value++) hot.Update(Documents(value));
                using var reader = engine.Query("hot", new Query());
                var length = new FileInfo(LogName(source)).Length;
                engine.Checkpoint();
                Task.Run(() => cold.Update(Documents(21))).GetAwaiter().GetResult();
                if (new FileInfo(LogName(source)).Length > length + 3 * 8192)
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
                if (afterAbandoned != beforeAbandoned + Constants.PAGE_SIZE)
                    throw new Exception("The abandoned transaction did not reuse reclaimed WAL slots: " +
                        (afterAbandoned - beforeAbandoned));
                if (provisionalLow >= abandonedHigh || finalLow <= abandonedHigh)
                    throw new Exception($"The fixture did not rebase the delayed transaction: " +
                        $"{provisionalLow}, {abandonedHigh}, {finalLow}");

                var ids = ReadTransactionIds(LogName(source), password);
                if (!ids.Contains(abandonedHigh) || ids.Last() != finalLow || ids.Max() != finalLow)
                    throw new Exception("The physical WAL tail does not dominate the abandoned transaction ID");
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

        private static uint[] ReadTransactionIds(string filename, string password)
        {
            using var file = new FileStream(filename, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            using var stream = password == null ? (Stream)file : new AesStream(password, file);
            var bytes = new byte[Constants.PAGE_SIZE];
            var page = new PageBuffer(bytes, 0, 0);
            var ids = new System.Collections.Generic.List<uint>();
            while (stream.ReadFully(bytes, 0, bytes.Length) == bytes.Length)
            {
                if (!page.IsBlank()) ids.Add(page.ReadUInt32(BasePage.P_TRANSACTION_ID));
            }
            return ids.ToArray();
        }
    }
}
