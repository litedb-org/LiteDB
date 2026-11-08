using LiteDB;

// This class references the v5 client contracts without implementing an engine.
// Its unchanged binary must run with the released DLL and all current variants.
public static class ReleasedCallers
{
    public static void Run()
    {
        var directory = Path.Combine(Path.GetTempPath(), "litedb-released-callers-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            foreach (var shared in new[] { false, true })
            {
                var filename = Path.Combine(directory, shared ? "shared.db" : "direct.db");
                var connection = $"Filename={filename};Connection={(shared ? "Shared" : "Direct")}";
                using (ILiteDatabase database = new LiteDatabase(connection))
                {
                    ILiteCollection<BsonDocument> rows = database.GetCollection("rows");
                    Check(database.BeginTrans(), "begin");
                    rows.Insert(new BsonDocument { ["_id"] = 1, ["value"] = "committed" });
                    Check(database.Commit(), "commit");
                    Check(database.BeginTrans(), "rollback begin");
                    rows.Insert(new BsonDocument { ["_id"] = 2, ["value"] = "aborted" });
                    Check(database.Rollback(), "rollback");
                    Check(rows.Count() == 1 && rows.FindById(1)["value"].AsString == "committed", "visible rows");
                }
                for (var pass = 0; pass < 2; pass++)
                {
                    using ILiteDatabase reopened = new LiteDatabase(connection);
                    var rows = reopened.GetCollection("rows");
                    Check(rows.Count() == 1 && rows.FindById(2) == null, "cold rows");
                    Console.WriteLine($"caller:{shared}:{pass}:{rows.FindById(1)["value"].AsString}");
                }
            }
        }
        finally { Directory.Delete(directory, true); }
        Console.WriteLine("PASS released client callers");
    }

    private static void Check(bool value, string message)
    {
        if (!value) throw new Exception(message);
    }
}
