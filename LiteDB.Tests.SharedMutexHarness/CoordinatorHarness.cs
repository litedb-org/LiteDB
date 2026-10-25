#pragma warning disable LITEDB_EXPERIMENTAL_COORDINATOR
using LiteDB;
using LiteDB.Engine;

/// <summary>Child-process roles for the experimental coordinator tests.</summary>
internal static class CoordinatorHarness
{
    internal static bool TryRun(string mode, string filename, string? password, string[] args)
    {
        switch (mode)
        {
            case "coord-host":
                Host(filename, password);
                return true;
            case "coord-writer":
                Writer(filename, password, args[4]);
                return true;
            default:
                return false;
        }
    }

    /// <summary>
    /// Become the coordinator, leave an uncommitted transaction open and wait. The
    /// test either kills this process or releases it, which rolls back and exits.
    /// </summary>
    private static void Host(string filename, string? password)
    {
        using var engine = new CoordinatedEngine(new EngineSettings { Filename = filename, Password = password });
        if (!engine.IsCoordinator) throw new InvalidOperationException("coord-host did not become the coordinator");
        using var database = new LiteDatabase(engine, disposeOnClose: false);
        database.GetCollection("docs").EnsureIndex("n", "$.n");
        // A separate collection: the open transaction must not block the writers.
        var pending = database.GetCollection("pending");
        database.BeginTrans();
        for (var i = 0; i < 20; i++) pending.Insert(new BsonDocument { ["_id"] = "uncommitted-" + i, ["n"] = -1 });
        Console.WriteLine("ready");
        Console.ReadLine();
        database.Rollback();
        Console.WriteLine("done");
    }

    /// <summary>
    /// Insert "prefix-i" documents one per call and report each outcome: "ack" once the
    /// insert returned, "unknown" when the coordinator died during it.
    /// </summary>
    private static void Writer(string filename, string? password, string spec)
    {
        var parts = spec.Split(':');
        var prefix = parts[0];
        var count = int.Parse(parts[1]);
        using var engine = new CoordinatedEngine(new EngineSettings { Filename = filename, Password = password });
        using var database = new LiteDatabase(engine, disposeOnClose: false);
        var docs = database.GetCollection("docs");
        for (var i = 0; i < count; i++)
        {
            var id = prefix + "-" + i;
            try
            {
                docs.Insert(new BsonDocument { ["_id"] = id, ["n"] = i });
                Console.WriteLine("ack:" + id);
            }
            catch (LiteException ex) when (ex.Message.Contains("outcome is unknown"))
            {
                Console.WriteLine("unknown:" + id);
            }
        }
        // Reads through this process' role, whichever it has now.
        Console.WriteLine("count:" + docs.Count(Query.StartsWith("_id", prefix + "-")));
        Console.WriteLine("done");
    }
}
