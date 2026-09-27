namespace LiteDB.Fuzz.Targets;

internal sealed class ThreadedSnapshotFuzzer : IFuzzTarget
{
    public string Name => "threaded-snapshot";
    public string Description => "Barrier-forced same-process snapshots across completed commits and checkpoint/page reuse.";

    public Task RunAsync(FuzzContext context)
    {
        var file = context.RegisterFile(Path.Combine(context.DirectoryPath, "threaded-snapshot.db"));
        using var db = new LiteDatabase(new ConnectionString
        {
            Filename = file, TransactionPageLimit = 3, DurableCommits = true
        });
        db.CheckpointSize = 0;
        var rows = db.GetCollection("rows");
        rows.EnsureIndex("value", "Value");
        var model = Enumerable.Range(1, 30).ToDictionary(id => id, id => Document(id, id * 10, 0));
        rows.Insert(model.Values.Select(Clone));
        db.Checkpoint();

        var rounds = 0;
        while (context.Next())
        {
            var expectedA = Snapshot(model.Values.OrderBy(document => document["_id"]));
            using var readerA = rows.Query().OrderBy("_id").ToEnumerable().GetEnumerator();
            context.Check(readerA.MoveNext(), "Reader A could not pin version N.");

            var versionOne = CloneModel(model);
            Mutate(versionOne, context.Steps * 2 + 1);
            var writerOne = Task.Run(() => Commit(db, versionOne));
            RequireCompleted(context, writerOne, "N+1 writer", readerA);
            model = versionOne;

            var expectedB = Snapshot(model.Values.OrderBy(document => document["Value"]).ThenBy(document => document["_id"]));
            using var readerB = rows.Query().OrderBy("Value").ThenBy("_id").ToEnumerable().GetEnumerator();
            context.Check(readerB.MoveNext(), "Reader B could not pin version N+1.");

            var versionTwo = CloneModel(model);
            Mutate(versionTwo, context.Steps * 2 + 2);
            var writerTwo = Task.Run(() => Commit(db, versionTwo));
            RequireCompleted(context, writerTwo, "N+2 writer", readerA, readerB);
            model = versionTwo;

            using var checkpointStarted = new ManualResetEventSlim();
            var checkpoint = Task.Run(() => { checkpointStarted.Set(); db.Checkpoint(); });
            context.Check(checkpointStarted.Wait(TimeSpan.FromSeconds(5)), "Checkpoint did not start with old readers alive.");

            context.Check(Consume(readerA) == expectedA, "Reader A observed data outside version N.");
            context.Check(Consume(readerB) == expectedB, "Reader B observed data outside version N+1.");
            var expectedNew = Snapshot(model.Values.OrderBy(document => document["_id"]));
            context.Check(Snapshot(rows.Query().OrderBy("_id").ToArray()) == expectedNew,
                "A new reader did not observe version N+2.");
            context.Check(checkpoint.Wait(TimeSpan.FromSeconds(15)),
                "Checkpoint did not finish after old snapshots were consumed.");
            checkpoint.GetAwaiter().GetResult();
            context.ObserveNovelty("threaded-snapshot", rounds % 4, model.Count / 8,
                checkpoint.IsCompleted);
            rounds++;
        }
        db.Checkpoint();
        DatabaseIntegrityVerifier.Verify(context, file);
        context.Metrics["forcedSnapshotRounds"] = rounds;
        context.Metrics["commitsWhileOldReadersLive"] = rounds * 2;
        return Task.CompletedTask;
    }

    private static void RequireCompleted(FuzzContext context, Task writer, string name,
        params IDisposable[] liveReaders)
    {
        context.Check(liveReaders.Length > 0 && writer.Wait(TimeSpan.FromSeconds(10)),
            $"{name} did not complete while older readers remained alive.");
        writer.GetAwaiter().GetResult();
    }

    private static void Commit(LiteDatabase db, Dictionary<int, BsonDocument> desired)
    {
        if (!db.BeginTrans()) throw new InvalidOperationException("Writer could not begin its transaction.");
        try
        {
            var rows = db.GetCollection("rows");
            var desiredIds = desired.Keys.ToHashSet();
            foreach (var id in rows.Query().OrderBy("_id").ToArray().Select(value => value["_id"].AsInt32))
                if (!desiredIds.Contains(id)) rows.Delete(id);
            foreach (var document in desired.Values) rows.Upsert(Clone(document));
            if (!db.Commit()) throw new InvalidOperationException("Writer could not commit.");
        }
        catch
        {
            db.Rollback();
            throw;
        }
    }

    private static void Mutate(Dictionary<int, BsonDocument> model, int version)
    {
        model.Remove(version % 30 + 1);
        var id = 1000 + version;
        model[id] = Document(id, version % 7 - 3, 9000 + version % 5 * 3000);
        var existing = model.Keys.OrderBy(value => value).First();
        model[existing] = Document(existing, -version, 12000);
    }

    private static string Consume(IEnumerator<BsonDocument> reader)
    {
        var documents = new List<BsonDocument> { Clone(reader.Current) };
        while (reader.MoveNext()) documents.Add(Clone(reader.Current));
        return Snapshot(documents);
    }

    private static string Snapshot(IEnumerable<BsonDocument> documents) => string.Join("|",
        documents.Select(document => Convert.ToBase64String(BsonSerializer.Serialize(document))));

    private static BsonDocument Document(int id, int value, int payload) => new()
    {
        ["_id"] = id, ["Value"] = value, ["Payload"] = new byte[payload]
    };

    private static BsonDocument Clone(BsonDocument document) =>
        BsonSerializer.Deserialize(BsonSerializer.Serialize(document));

    private static Dictionary<int, BsonDocument> CloneModel(Dictionary<int, BsonDocument> model) =>
        model.ToDictionary(pair => pair.Key, pair => Clone(pair.Value));
}
