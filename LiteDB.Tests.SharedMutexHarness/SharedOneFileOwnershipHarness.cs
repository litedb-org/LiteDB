using System.Reflection;
using LiteDB;
using LiteDB.Client.Shared;
using LiteDB.Engine;

internal static class SharedOneFileOwnershipHarness
{
    internal static bool TryRun(string mode, string filename, string[] args)
    {
        if (mode != "one-file-ownership") return false;
        using var engine = new SharedEngine(new EngineSettings
        {
            Filename = filename, SharedOneFile = true, Password = args[3] == "-" ? null : args[3],
            TransactionPageLimit = 1
        });
        using var db = new LiteDatabase(engine, disposeOnClose: false);
        db.CheckpointSize = 0;
        var rows = db.GetCollection("docs");
        if (rows.FindById(1)["value"].AsInt32 != 1) throw new Exception("Missing seed");
        Console.WriteLine("attached");
        string? command;
        while ((command = Console.ReadLine()) != null)
        {
            if (command == "close") break;
            if (command == "own")
            {
                Require(db.BeginTrans());
                rows.Insert(Row(1000));
                Console.WriteLine("owned");
                continue;
            }
            if (command == "commit" || command == "rollback")
            {
                Require(command == "commit" ? db.Commit() : db.Rollback());
                Console.WriteLine("released");
                continue;
            }
            if (command == "write")
            {
                Console.WriteLine("attempting");
                Require(db.BeginTrans());
                for (var id = 3000; id < 3020; id++) rows.Insert(Row(id));
                Require(db.Commit());
                Console.WriteLine("written");
                continue;
            }
            if (!command.StartsWith("retry-", StringComparison.Ordinal)) throw new Exception(command);
            Require(db.BeginTrans());
            for (var retry = 0; retry < 3; retry++)
            {
                Require(!db.BeginTrans());
                rows.Insert(Row(1000 + retry));
                try { rows.Insert(Row(1)); throw new Exception("Duplicate accepted"); }
                catch (LiteException error) when (error.ErrorCode == 110) { }
                if (command == "retry-failed")
                {
                    TransactionMonitor? monitor = null;
                    engine.BeforeCountingUser = () =>
                    {
                        monitor = Field<TransactionMonitor>(Field<LiteEngine>(engine, "_engine"), "_monitor");
                        monitor.BeforeTransactionRegistration = () => throw new IOException("retry begin failure");
                    };
                    try
                    {
                        try { db.BeginTrans(); throw new Exception("Retry failure missed"); }
                        catch (IOException error) when (error.Message == "retry begin failure") { }
                    }
                    finally
                    {
                        engine.BeforeCountingUser = null;
                        if (monitor != null) monitor.BeforeTransactionRegistration = null;
                    }
                    if (!LiteDB.Utils.Reachability.Snapshot().ContainsKey("fault-point:BeforeTransactionRegistration"))
                        throw new Exception("Retry registration fault marker was not reached");
                    Console.WriteLine("retry-failed");
                }
                Require(db.BeginTrans());
            }
            rows.Insert(Row(2000));
            Require(command == "retry-rollback" ? db.Rollback() : db.Commit());
            Require(!db.Commit());
            Require(!db.Rollback());
            Console.WriteLine("released");
        }
        Console.WriteLine("done");
        return true;
    }

    private static void Require(bool condition)
    {
        if (!condition) throw new Exception("Unexpected transaction ownership result");
    }
    private static T Field<T>(object instance, string name) =>
        (T)instance.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(instance)!;
    private static BsonDocument Row(int id) => new()
    { ["_id"] = id, ["value"] = id, ["payload"] = new string('x', 20000) };
}
