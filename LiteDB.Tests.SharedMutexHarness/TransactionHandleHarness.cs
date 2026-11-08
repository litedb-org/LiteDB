using LiteDB;
using LiteDB.Client.Shared;
using System.Reflection;
using LiteDB.Engine;

internal static class TransactionHandleHarness
{
    internal static bool TryRun(string mode, string filename, string password, string[] args)
    {
        if (mode == "handle-turnstile-mapped" || mode == "handle-turnstile-legacy")
        {
            using var owner = new SharedEngine(new EngineSettings { Filename = filename, SharedOneFile = mode.EndsWith("mapped", StringComparison.Ordinal) });
            var name = (string)typeof(SharedEngine).GetField("_mutexName", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(owner);
            using var turn = SharedMutexFactory.Create(name + ".Turn");
            turn.WaitOne(); Console.WriteLine("held");
            Console.ReadLine(); turn.ReleaseMutex(); Console.WriteLine("done");
            return true;
        }
        if (mode == "handle-reuse-mapped" || mode == "handle-reuse-legacy")
        {
            using var core = new SharedEngine(new EngineSettings { Filename = filename, Password = password, SharedOneFile = mode == "handle-reuse-mapped" });
            using var writer = new LiteDatabase(core, disposeOnClose: false);
            using (var transaction = writer.BeginTransaction())
            {
                var round = int.Parse(args[4]);
                var rows = transaction.GetCollection("rows");
                var updated = rows.FindAll().ToList();
                foreach (var row in updated) row["value"] = round;
                if (rows.Update(updated) != 64) throw new InvalidOperationException("Peer did not update every row");
                if (!rows.DropIndex("idx" + (round - 1))) throw new InvalidOperationException("Previous index missing");
                if (!rows.EnsureIndex("idx" + round, "$.value")) throw new InvalidOperationException("Index not created");
                transaction.Commit();
            }
            writer.Checkpoint();
            Console.WriteLine("done");
            return true;
        }
        if (mode != "handle-direct" && mode != "handle-mapped" && mode != "handle-legacy" && mode != "handle-readonly-mapped" && mode != "handle-readonly-legacy") return false;
        using ILiteEngine engine = mode == "handle-direct"
            ? new LiteEngine(new EngineSettings { Filename = filename, Password = password, TransactionPageLimit = 1 })
            : new SharedEngine(new EngineSettings
        { Filename = filename, Password = password, SharedOneFile = mode.EndsWith("mapped", StringComparison.Ordinal), ReadOnly = mode.Contains("readonly"), TransactionPageLimit = 1 });
        using var db = new LiteDatabase(engine, disposeOnClose: false);
        // Safely refresh persisted TIMEOUT before contention starts.
        _ = db.Timeout;
        ILiteTransaction tx = null;
        Console.WriteLine("ready");
        string command;
        while ((command = Console.ReadLine()) != null)
        {
            switch (command)
            {
                case "begin":
                    Console.WriteLine("attempting");
                    try { tx = db.BeginTransaction(); Console.WriteLine("begun"); }
                    catch (LiteException error) when (error.ErrorCode == 120) { Console.WriteLine("timeout"); }
                    break;
                case "write":
                    var logPath = FileHelper.GetLogFile(filename);
                    var before = System.IO.File.Exists(logPath) ? new System.IO.FileInfo(logPath).Length : 0;
                    var rows = tx.GetCollection("rows");
                    rows.Insert(new BsonDocument { ["_id"] = 2, ["value"] = 20, ["payload"] = new string('x', 50000) });
                    // The next document's safepoint spills row 2 without committing it.
                    rows.Insert(new BsonDocument { ["_id"] = 0 });
                    rows.Delete(0);
                    if (new System.IO.FileInfo(logPath).Length <= before)
                        throw new InvalidOperationException("The open handle did not spill uncommitted WAL frames.");
                    Console.WriteLine("written"); break;
                case "commit": tx.Commit(); Console.WriteLine("committed"); break;
                case "close": tx?.Dispose(); Console.WriteLine("done"); return true;
                default: throw new InvalidOperationException(command);
            }
        }
        return true;
    }
}
