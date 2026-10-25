using LiteDB;
using LiteDB.Engine;

internal static class SharedSafetyHarness
{
    internal static bool TryRun(string mode, string filename, string? password, string[] args)
    {
        if (mode != "shared-relative") return false;
        var originalDirectory = Directory.GetCurrentDirectory();
        try
        {
            Directory.SetCurrentDirectory(Path.GetDirectoryName(filename)!);
            using var engine = new SharedEngine(new EngineSettings
            {
                Filename = Path.GetFileName(filename), Password = password
            });
            using var database = new LiteDatabase(engine, disposeOnClose: false);
            using var snapshot = args[4] == "after" ? engine.Query("docs", new Query()) : null;
            Directory.SetCurrentDirectory(Path.Combine(Path.GetDirectoryName(filename)!, "other"));
            database.GetCollection("docs").Update(Enumerable.Range(0, 64).Select(id =>
                new BsonDocument { ["_id"] = id, ["value"] = 1, ["payload"] = new string('x', 3000) }));
            if (snapshot != null)
            {
                var count = 0;
                while (snapshot.Read())
                {
                    if (snapshot.Current["value"].AsInt32 != 0) throw new Exception("Snapshot changed");
                    count++;
                }
                if (count != 64) throw new Exception("Snapshot lost rows");
            }
            Console.WriteLine("done");
        }
        finally { Directory.SetCurrentDirectory(originalDirectory); }
        return true;
    }
}
