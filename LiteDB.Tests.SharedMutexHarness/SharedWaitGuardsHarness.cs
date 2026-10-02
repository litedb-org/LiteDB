using System.Reflection;
using LiteDB;
using LiteDB.Engine;

/// <summary>
/// Owners for SharedWriterTimeout tests in another process (#3080): a legacy transaction or the
/// file's turnstile, held until the parent says how to end it (or kills this process).
/// </summary>
internal static class SharedWaitGuardsHarness
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    internal static bool TryRun(string mode, string filename, string? password)
    {
        if (mode == "turnstile-hold")
        {
            // The engine opens the file's turnstile by name, as every Shared connection does;
            // only this process holds it, and it never opens the database.
            using var engine = new SharedEngine(new EngineSettings { Filename = filename, Password = password });
            var turnstile = typeof(SharedEngine).GetField("_turnstile", Private)!.GetValue(engine)!;
            var turn = (Mutex)turnstile.GetType().GetField("_turn", Private)!.GetValue(turnstile)!;
            try { turn.WaitOne(); }
            catch (AbandonedMutexException) { }
            Console.WriteLine("ready");
            Console.ReadLine();
            turn.ReleaseMutex();
            Console.WriteLine("done");
            return true;
        }
        if (mode == "legacy-hold")
        {
            // As handle-hold, but a legacy transaction: id 2 spills to the WAL uncommitted.
            using var db = new LiteDatabase(new ConnectionString { Filename = filename, Password = password,
                Connection = ConnectionType.Shared, TransactionPageLimit = 1 });
#pragma warning disable CS0618
            db.BeginTrans();
            db.GetCollection("rows").Insert(new BsonDocument { ["_id"] = 2, ["value"] = 84, ["payload"] = new string('x', 50000) });
            Console.WriteLine("ready");
            if (Console.ReadLine() == "commit") db.Commit();
            else db.Rollback();
#pragma warning restore CS0618
            Console.WriteLine("done");
            return true;
        }
        return false;
    }
}
