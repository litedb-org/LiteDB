using LiteDB;

internal static class DirectHostHarness
{
    internal static bool TryRun(string mode, string filename, string password)
    {
        if (!mode.StartsWith("direct-host-", StringComparison.Ordinal)) return false;
        if (mode == "direct-host-hold")
        {
            using var database = new LiteDatabase(new ConnectionString { Filename = filename, Password = password });
            database.CheckpointSize = 0;
            database.GetCollection("docs").Insert(new BsonDocument { ["_id"] = 1, ["value"] = "acknowledged" });
            database.GetCollection("docs").EnsureIndex("value");
            database.BeginTrans();
            database.GetCollection("docs").Insert(new BsonDocument { ["_id"] = 2 });
            Console.WriteLine("ready");
            Console.ReadLine();
            database.Rollback();
            Console.WriteLine("done");
            return true;
        }
        if (mode == "direct-host-rebuild-refused")
        {
            foreach (var connection in new[] { ConnectionType.Direct, ConnectionType.Shared })
            {
                try
                {
                    using var database = new LiteDatabase(new ConnectionString { Filename = filename, Password = password, Connection = connection });
                    throw new InvalidOperationException("Incomplete rebuild unexpectedly opened");
                }
                catch (LiteException error) when (error.ErrorCode == LiteException.REBUILD_INCOMPLETE) { }
            }
            Console.WriteLine("done");
            return true;
        }
        var shared = mode == "direct-host-shared-refused";
        var refused = mode.EndsWith("refused", StringComparison.Ordinal);
        try
        {
            using var database = new LiteDatabase(new ConnectionString { Filename = filename, Password = password,
                Connection = shared ? ConnectionType.Shared : ConnectionType.Direct });
            database.GetCollection("docs").FindAll().ToArray();
            if (refused) throw new InvalidOperationException("Conflicting open unexpectedly succeeded");
        }
        catch (IOException error) when (refused)
        {
            if (!error.ToString().Contains("admission", StringComparison.OrdinalIgnoreCase)) throw;
        }
        Console.WriteLine("done");
        return true;
    }
}
