using LiteDB;
using LiteDB.Engine;

internal static class MvccHarness
{
    internal static bool TryRun(string[] args)
    {
        if (args.Length == 0 || args[0] != "mvcc") return false;
        var expectedRuntime = Environment.GetEnvironmentVariable("LITEDB_MVCC_RUNTIME");
        var expectedArchitecture = Environment.GetEnvironmentVariable("LITEDB_MVCC_ARCHITECTURE");
        if (expectedRuntime != null && Environment.Version.ToString() != expectedRuntime)
            throw new InvalidOperationException("MVCC child runtime does not match its test host");
        if (expectedArchitecture != null &&
            System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString() != expectedArchitecture)
            throw new InvalidOperationException("MVCC child architecture does not match its test host");
        var mode = args[1];
        var filename = args[2];
        var password = args[3] == "-" ? null : args[3];
        if (SharedSafetyHarness.TryRun(mode, filename, password, args)) return true;
        if (SharedStorageHarness.TryRun(mode, filename, password, args)) return true;
        if (CoordinatorHarness.TryRun(mode, filename, password, args)) return true;
        var settings = new EngineSettings
        {
            Filename = filename, Password = password, TransactionPageLimit = 1, CacheSize = 8192
        };
        if (mode == "crash-checkpoint")
        {
            Action<string> hook = stage =>
            {
                if (stage != args[4]) return;
                Console.WriteLine("ready");
                Console.ReadLine();
            };
            typeof(EngineSettings).GetProperty("CheckpointStage",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                .SetValue(settings, hook);
        }
        using var engine = new SharedEngine(settings);
        using var database = new LiteDatabase(engine, disposeOnClose: false);
        var collection = database.GetCollection("docs");
        switch (mode)
        {
            case "seed":
                database.Pragma(Pragmas.CHECKPOINT, 0);
                database.GetCollection("cold").Insert(Documents(0));
                collection.Insert(Documents(0));
                Console.WriteLine("done");
                break;
            case "history":
                for (var value = 1; value <= 20; value++) collection.Update(Documents(value));
                Console.WriteLine("done");
                break;
            case "write-cold":
                database.GetCollection("cold").Update(Documents(int.Parse(args[4])));
                Console.WriteLine("done");
                break;
            case "uncommitted-cold":
                database.BeginTrans();
                database.GetCollection("cold").Update(Documents(99));
                Console.WriteLine("ready");
                Console.ReadLine();
                break;
            case "read-cold":
                if (database.GetCollection("cold").FindAll().Any(doc => doc["value"].AsInt32 != int.Parse(args[4])))
                    throw new InvalidOperationException("Incorrect recovered cold collection");
                Console.WriteLine("done");
                break;
            case "write":
                collection.Update(Documents(int.Parse(args[4])));
                Console.WriteLine("done");
                break;
            case "insert":
                var start = int.Parse(args[4]);
                for (var i = start; i < start + 20; i++) collection.Insert(new BsonDocument { ["_id"] = i });
                Console.WriteLine("done");
                break;
            case "uncommitted":
                database.BeginTrans();
                collection.Update(Documents(99));
                Console.WriteLine("ready");
                Console.ReadLine();
                break;
            case "crash-checkpoint":
            case "checkpoint":
                database.Checkpoint();
                Console.WriteLine("done");
                break;
            case "read":
            case "hold":
            case "hold-cold":
                using (var reader = engine.Query(mode == "hold-cold" ? "cold" : "docs", new Query()))
                {
                    if (mode == "hold" || mode == "hold-cold")
                    {
                        Console.WriteLine("ready");
                        Console.ReadLine();
                    }
                    var values = new List<int>();
                    while (reader.Read()) values.Add(reader.Current["value"].AsInt32);
                    if (values.Count != 64 || values.Distinct().Count() != 1)
                        throw new InvalidOperationException("Inconsistent snapshot: " + string.Join(",", values));
                    Console.WriteLine("value:" + values[0]);
                }
                break;
            default: throw new ArgumentException(mode);
        }
        return true;
    }

    private static IEnumerable<BsonDocument> Documents(int value) => Enumerable.Range(0, 64).Select(id =>
        new BsonDocument { ["_id"] = id, ["value"] = value, ["payload"] = new string('x', 3000) });
}
