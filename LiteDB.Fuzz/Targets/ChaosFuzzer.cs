using LiteDB.Engine;

namespace LiteDB.Fuzz.Targets;

internal sealed class ChaosFuzzer : IFuzzTarget
{
    public string Name => "chaos";
    public string Description => "Combined CRUD, bulk, index, transaction, SQL, storage, rebuild, reopen, and auto-checkpoint state machine.";

    public Task RunAsync(FuzzContext context)
    {
        var file = context.RegisterFile(Path.Combine(context.DirectoryPath, "chaos.db"));
        var connection = new ConnectionString
        {
            Filename = file, Password = "fuzz", TransactionPageLimit = 2, CacheSize = 512 * 1024,
            DurableCommits = true
        };
        var model = new SortedDictionary<int, BsonDocument>();
        var files = new Dictionary<string, byte[]>();
        var db = new LiteDatabase(connection);
        db.CheckpointSize = 1;
        var operations = new HashSet<int>();
        try
        {
            while (context.Next())
            {
                var rows = db.GetCollection("rows");
                var operation = context.Random.Next(16);
                operations.Add(operation);
                switch (operation)
                {
                    case 0:
                    case 1:
                        Upsert(context, rows, model);
                        break;
                    case 2:
                        UpdateMany(context, rows, model);
                        break;
                    case 3:
                        DeleteMany(context, rows, model);
                        break;
                    case 4:
                        Transaction(context, db, model);
                        break;
                    case 5:
                        if (context.Steps % 2 == 0)
                        {
                            rows.EnsureIndex("value", "value");
                            rows.EnsureIndex("unique", "unique", true);
                        }
                        else
                        {
                            rows.DropIndex("value");
                            rows.DropIndex("unique");
                        }
                        break;
                    case 6:
                        Storage(context, db, files);
                        break;
                    case 7:
                        db.Checkpoint();
                        db.Dispose();
                        db = new LiteDatabase(connection);
                        db.CheckpointSize = 1;
                        break;
                    case 8:
                        var nextPassword = connection.Password == "fuzz" ? "fuzz-next" : "fuzz";
                        db.Rebuild(new RebuildOptions
                        {
                            Collation = context.Steps % 2 == 0 ? Collation.Binary : new Collation("en-US/IgnoreCase"),
                            Password = nextPassword
                        });
                        connection.Password = nextPassword;
                        break;
                    case 9:
                        db.UserVersion = context.Steps;
                        db.UtcDate = context.Steps % 2 == 0;
                        db.CheckpointSize = 1 + context.Steps % 3;
                        break;
                    case 10:
                        SqlMutation(context, db, model);
                        break;
                    case 11:
                        ReadPressure(context, rows, model);
                        break;
                    case 12:
                        var canRename = db.CollectionExists("rows");
                        var renamed = db.RenameCollection("rows", "rows_renamed");
                        context.Check(renamed == canRename, "Chaos collection rename returned an impossible result.");
                        if (renamed)
                        {
                            context.Check(db.GetCollection("rows_renamed").Count() == model.Count,
                                "Renamed chaos collection changed count.");
                            context.Check(db.RenameCollection("rows_renamed", "rows"),
                                "Chaos collection rename-back failed.");
                        }
                        break;
                    case 13:
                        var existed = db.CollectionExists("rows");
                        context.Check(db.DropCollection("rows") == existed,
                            "Chaos collection drop returned an impossible result.");
                        model.Clear();
                        break;
                    case 14:
                        LongReaderMutation(context, db, model);
                        break;
                    case 15:
                        FailingStorage(context, db, files);
                        break;
                }
                Validate(context, db, model, files);
                context.ObserveNovelty("chaos-state", operation, model.Count / 8, files.Count, db.CheckpointSize);
            }
            db.Checkpoint();
            DatabaseIntegrityVerifier.Verify(context, file, connection.Password);
            if (context.Steps >= 160)
                context.Check(operations.Count == 16, "Combined chaos campaign missed an operation family.");
            context.Metrics["chaosOperationFamilies"] = operations.Count;
            context.Metrics["documents"] = model.Count;
            context.Metrics["files"] = files.Count;
        }
        finally { db.Dispose(); }
        return Task.CompletedTask;
    }

    private static void Upsert(FuzzContext context, ILiteCollection<BsonDocument> rows,
        SortedDictionary<int, BsonDocument> model)
    {
        var id = context.Random.Next(1, 80);
        var document = Document(context.Random, id);
        rows.Upsert(Clone(document));
        model[id] = document;
    }

    private static void UpdateMany(FuzzContext context, ILiteCollection<BsonDocument> rows,
        SortedDictionary<int, BsonDocument> model)
    {
        var pivot = context.Random.Next(-10, 11);
        var parameters = new BsonDocument { ["p"] = pivot };
        var count = rows.UpdateMany(BsonExpression.Create("{ value: value + 3, hits: hits + 1 }", parameters),
            BsonExpression.Create("value >= @p AND value <= @p + 4", parameters));
        var expected = 0;
        foreach (var pair in model.Where(pair => pair.Value["value"] >= pivot && pair.Value["value"] <= pivot + 4).ToArray())
        {
            pair.Value["value"] = pair.Value["value"].AsInt32 + 3;
            pair.Value["hits"] = pair.Value["hits"].AsInt32 + 1;
            expected++;
        }
        context.Check(count == expected, "Chaos UpdateMany count differed from its independent model.");
    }

    private static void DeleteMany(FuzzContext context, ILiteCollection<BsonDocument> rows,
        SortedDictionary<int, BsonDocument> model)
    {
        var tag = context.Random.Next(4);
        var expression = BsonExpression.Create("tags ANY = @tag", new BsonDocument { ["tag"] = tag });
        var count = rows.DeleteMany(expression);
        var ids = model.Where(pair => pair.Value["tags"].AsArray.Contains(tag)).Select(pair => pair.Key).ToArray();
        foreach (var id in ids) model.Remove(id);
        context.Check(count == ids.Length, "Chaos DeleteMany count differed from its independent model.");
    }

    private static void Transaction(FuzzContext context, LiteDatabase db, SortedDictionary<int, BsonDocument> model)
    {
        var pending = model.ToDictionary(pair => pair.Key, pair => Clone(pair.Value));
        db.BeginTrans();
        var rows = db.GetCollection("rows");
        for (var i = 0; i < 3; i++)
        {
            var id = context.Random.Next(1, 80);
            var document = Document(context.Random, id);
            rows.Upsert(Clone(document));
            pending[id] = document;
        }
        if (context.Random.Next(2) == 0)
        {
            db.Commit();
            model.Clear();
            foreach (var pair in pending) model[pair.Key] = pair.Value;
        }
        else db.Rollback();
    }

    private static void Storage(FuzzContext context, LiteDatabase db, Dictionary<string, byte[]> files)
    {
        var id = "f" + context.Random.Next(5);
        if (context.Random.Next(3) == 0)
        {
            var expected = files.Remove(id);
            context.Check(db.FileStorage.Delete(id) == expected, "Chaos storage delete differed from model.");
            return;
        }
        var bytes = new byte[context.Random.Next(0, 18000)];
        context.Random.NextBytes(bytes);
        using var source = new MemoryStream(bytes);
        db.FileStorage.Upload(id, id + ".bin", source);
        files[id] = bytes;
    }

    private static void SqlMutation(FuzzContext context, LiteDatabase db, SortedDictionary<int, BsonDocument> model)
    {
        var id = 100 + context.Random.Next(20);
        if (model.ContainsKey(id)) return;
        var document = Document(context.Random, id);
        using var reader = db.Execute("INSERT INTO rows VALUES " + JsonSerializer.Serialize(document));
        while (reader.Read()) { }
        model[id] = document;
    }

    private static void ReadPressure(FuzzContext context, ILiteCollection<BsonDocument> rows,
        SortedDictionary<int, BsonDocument> model)
    {
        var actual = rows.Query().Where("value >= @0", -5).OrderBy("value").ThenBy("_id").ToArray();
        var expected = model.Values.Where(row => row["value"] >= -5)
            .OrderBy(row => row["value"]).ThenBy(row => row["_id"]).ToArray();
        context.Check(actual.Select(row => row["_id"]).SequenceEqual(expected.Select(row => row["_id"])),
            "Chaos indexed read differed from model.");
    }

    private static void LongReaderMutation(FuzzContext context, LiteDatabase db,
        SortedDictionary<int, BsonDocument> model)
    {
        if (model.Count == 0)
        {
            var initial = Document(context.Random, 1);
            db.GetCollection("rows").Upsert(Clone(initial));
            model[1] = initial;
        }
        var expected = model.Values.Select(Clone).ToArray();
        using var reader = db.GetCollection("rows").Query().OrderBy("_id").ToEnumerable().GetEnumerator();
        context.Check(reader.MoveNext(), "Chaos long reader could not pin a snapshot.");
        var id = 200 + context.Steps;
        var added = Document(context.Random, id);
        var writer = Task.Run(() => db.GetCollection("rows").Upsert(Clone(added)));
        context.Check(writer.Wait(TimeSpan.FromSeconds(10)),
            "Chaos writer did not complete while its old reader remained live.");
        writer.GetAwaiter().GetResult();
        model[id] = added;
        var actual = new List<BsonDocument> { Clone(reader.Current) };
        while (reader.MoveNext()) actual.Add(Clone(reader.Current));
        context.Check(actual.Count == expected.Length && actual.Zip(expected,
            (left, right) => BsonSerializer.Serialize(left).SequenceEqual(BsonSerializer.Serialize(right))).All(equal => equal),
            "Chaos long reader observed a later version.");
    }

    private static void FailingStorage(FuzzContext context, LiteDatabase db, Dictionary<string, byte[]> files)
    {
        var id = "faulted";
        if (!files.TryGetValue(id, out var original))
        {
            original = Enumerable.Range(0, 5000).Select(index => (byte)index).ToArray();
            using var initial = new MemoryStream(original);
            db.FileStorage.Upload(id, "original.bin", initial);
            files[id] = original;
        }
        var replacement = new byte[12000];
        context.Random.NextBytes(replacement);
        Exception failure = null;
        try
        {
            using var source = new ChaosThrowingStream(replacement);
            db.FileStorage.Upload(id, "replacement.bin", source);
        }
        catch (Exception error) { failure = error; }
        context.Check(failure is IOException, "Chaos throwing storage source did not preserve IOException.");
        using var output = new MemoryStream();
        db.FileStorage.Download(id, output);
        context.Check(output.ToArray().SequenceEqual(original),
            "Chaos throwing storage source partially replaced an acknowledged file.");
    }

    private static void Validate(FuzzContext context, LiteDatabase db, SortedDictionary<int, BsonDocument> model,
        Dictionary<string, byte[]> files)
    {
        var actual = db.GetCollection("rows").Query().OrderBy("_id").ToArray();
        context.Check(actual.Length == model.Count, "Chaos document count differed from model.");
        for (var i = 0; i < actual.Length; i++)
            context.Check(BsonSerializer.Serialize(actual[i]).SequenceEqual(BsonSerializer.Serialize(model.Values.ElementAt(i))),
                $"Chaos document {i} differed from model.");
        foreach (var pair in files)
        {
            using var output = new MemoryStream();
            db.FileStorage.Download(pair.Key, output);
            context.Check(output.ToArray().SequenceEqual(pair.Value), $"Chaos file {pair.Key} differed from model.");
        }
    }

    private static BsonDocument Document(Random random, int id) => new()
    {
        ["_id"] = id, ["value"] = random.Next(-20, 21), ["hits"] = 0,
        ["tags"] = new BsonArray(random.Next(4), random.Next(4)), ["text"] = "row-" + id,
        ["unique"] = "u-" + id
    };

    private static BsonDocument Clone(BsonDocument value) =>
        BsonSerializer.Deserialize(BsonSerializer.Serialize(value));

}
