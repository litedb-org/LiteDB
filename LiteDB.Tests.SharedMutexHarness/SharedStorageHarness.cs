using LiteDB;
using LiteDB.Engine;

internal static class SharedStorageHarness
{
    internal static bool TryRun(string mode, string filename, string? password, string[] args)
    {
        if (!mode.StartsWith("storage-", StringComparison.Ordinal)) return false;
        var options = args[4].Split(',');
        var storage = Enum.Parse<CompactStorageMode>(options[0]);
        var revision = int.Parse(options[1]);
        using var engine = new SharedEngine(new EngineSettings
        {
            Filename = filename, Password = password, CompactStorage = storage,
            TransactionPageLimit = 1, CacheSize = 8192,
            ReadOnly = mode == "storage-readonly" || mode == "storage-reject"
        });
        using var database = new LiteDatabase(engine, disposeOnClose: false);
        var rows = database.GetCollection("docs");
        switch (mode)
        {
            case "storage-seed":
                database.CheckpointSize = 0;
                rows.Insert(Documents(0));
                rows.EnsureIndex("value");
                var cold = database.GetCollection("cold");
                cold.Insert(Documents(42));
                cold.EnsureIndex("value");
                database.Checkpoint();
                break;
            case "storage-write":
            case "storage-acknowledged":
            case "storage-uncommitted":
                database.BeginTrans();
                if (rows.Update(Documents(revision)) != 32) throw new Exception("Update lost rows");
                if (mode != "storage-uncommitted" && !database.Commit()) throw new Exception("Commit failed");
                if (mode != "storage-write") Pause();
                break;
            case "storage-hold":
                using (var snapshot = engine.Query("docs", new Query()))
                {
                    Pause();
                    var documents = new List<BsonDocument>();
                    while (snapshot.Read()) documents.Add(snapshot.Current.AsDocument);
                    AssertDocuments(documents, revision);
                }
                break;
            case "storage-verify":
            case "storage-readonly":
            case "storage-recovery":
                AssertCollection(rows, revision);
                AssertCollection(database.GetCollection("cold"), 42);
                var report = database.GetCollection("$database").FindAll().Single();
                if (!report["checksums"].AsBoolean || !report["durableLogFlush"].AsBoolean)
                    throw new Exception("Missing checksum or durability guarantee");
                if (mode == "storage-recovery" &&
                    (!report["recoveryInvalidWalTail"].AsBoolean || report["recoveryDiscardedWalBytes"].AsInt64 <= 0))
                    throw new Exception("Damaged WAL was not reported");
                break;
            case "storage-reject":
                try { rows.FindAll().ToArray(); }
                catch (LiteException ex) when (ex.ErrorCode == LiteException.CHECKSUM_MISMATCH &&
                    ex.Message.Contains("Data") && ex.Message.Contains(revision.ToString()))
                {
                    Console.WriteLine("done");
                    return true;
                }
                throw new Exception("Damaged data page was accepted");
            default: throw new ArgumentException(mode);
        }
        Console.WriteLine("done");
        return true;
    }

    private static void Pause()
    {
        Console.WriteLine("ready");
        Console.ReadLine();
    }

    private static IEnumerable<BsonDocument> Documents(int revision) => Enumerable.Range(0, 32).Select(id =>
        new BsonDocument { ["_id"] = id, ["value"] = revision, ["payload"] = Payload(id, revision) });

    private static string Payload(int id, int revision) => new string((char)('a' + id % 26), 3000) + ":" + revision;

    private static void AssertDocuments(IEnumerable<BsonDocument> documents, int revision)
    {
        var actual = documents.OrderBy(doc => doc["_id"].AsInt32).ToArray();
        if (actual.Length != 32) throw new Exception("Incorrect document count");
        for (var id = 0; id < 32; id++)
        {
            var doc = actual[id];
            if (doc["_id"].AsInt32 != id || doc["value"].AsInt32 != revision ||
                doc["payload"].AsString != Payload(id, revision)) throw new Exception("Incorrect document " + id);
        }
    }

    private static void AssertCollection(ILiteCollection<BsonDocument> rows, int revision)
    {
        AssertDocuments(rows.FindAll(), revision);
        AssertDocuments(rows.Find(Query.EQ("value", revision)), revision);
        if (rows.Find(Query.EQ("value", revision + 1)).Any()) throw new Exception("Stale index entries");
    }
}
