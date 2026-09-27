namespace LiteDB.Fuzz.Targets;

internal sealed class TransactionFuzzer : IFuzzTarget
{
    public string Name => "transaction";
    public string Description => "CRUD/DDL/explicit-transaction state machine against an independent committed-state model.";

    public Task RunAsync(FuzzContext context)
    {
        var filename = context.RegisterFile(Path.Combine(context.DirectoryPath, "transaction.db"));
        var connection = new ConnectionString
        {
            Filename = filename, TransactionPageLimit = 4, DurableCommits = true, AutoRebuild = false
        };
        var committed = NewModel("alpha", "beta");
        Dictionary<string, SortedDictionary<int, BsonDocument>> pending = null;
        var db = new LiteDatabase(connection);
        var integrityChecks = 0;
        var commits = 0;
        var rollbacks = 0;
        var reopens = 0;
        var schemaChanges = 0;
        try
        {
            while (context.Next())
            {
                var model = pending ?? committed;
                var names = model.Keys.OrderBy(x => x, StringComparer.Ordinal).ToArray();
                var name = names[context.Random.Next(names.Length)];
                var collection = db.GetCollection(name);
                var operation = context.Random.Next(16);
                context.Trace("state", new { operation, collection = name, transaction = pending != null });
                switch (operation)
                {
                    case 0:
                    case 1:
                        Insert(context, collection, model[name]);
                        break;
                    case 2:
                        Update(context, collection, model[name]);
                        break;
                    case 3:
                        Upsert(context, collection, model[name]);
                        break;
                    case 4:
                        Delete(context, collection, model[name]);
                        break;
                    case 5:
                        InsertBulk(context, collection, model[name]);
                        break;
                    case 6:
                        collection.EnsureIndex("value", "Value", unique: false);
                        break;
                    case 7:
                        collection.DropIndex("value");
                        break;
                    case 8 when pending == null:
                        context.Check(db.BeginTrans(), "BeginTrans did not start a transaction.");
                        pending = CloneModel(committed);
                        break;
                    case 9 when pending != null:
                        context.Check(db.Commit(), "Commit did not finish the transaction.");
                        committed = pending;
                        pending = null;
                        commits++;
                        break;
                    case 10 when pending != null:
                        context.Check(db.Rollback(), "Rollback did not finish the transaction.");
                        pending = null;
                        rollbacks++;
                        break;
                    case 11 when pending == null:
                        db.Checkpoint();
                        break;
                    case 12 when pending == null:
                        db.Dispose();
                        db = new LiteDatabase(connection);
                        reopens++;
                        break;
                    case 13:
                        DuplicateMustFail(context, collection, model[name]);
                        if (pending != null)
                        {
                            // Statement failures can invalidate the caller transaction.
                            // End either surviving or already-aborted state and keep only
                            // the independently modeled committed snapshot.
                            db.Rollback();
                            pending = null;
                        }
                        break;
                    case 14 when pending == null:
                        Rename(context, db, committed, name);
                        schemaChanges++;
                        break;
                    case 15 when pending == null:
                        DropAndCreate(context, db, committed, name);
                        schemaChanges++;
                        break;
                    default:
                        Validate(context, db, pending ?? committed);
                        break;
                }
                Validate(context, db, pending ?? committed);
                if (pending == null && context.Steps % 31 == 0)
                {
                    db.Checkpoint();
                    DatabaseIntegrityVerifier.Verify(context, filename);
                    integrityChecks++;
                }
            }
            if (pending != null)
            {
                if ((context.Seed & 1) == 0) { db.Commit(); committed = pending; }
                else db.Rollback();
            }
            db.Dispose();
            db = new LiteDatabase(connection);
            Validate(context, db, committed);
            db.Checkpoint();
            DatabaseIntegrityVerifier.Verify(context, filename);
            integrityChecks++;
            if (context.Steps >= 300)
                context.Check(commits > 0 && rollbacks > 0 && reopens > 0 && schemaChanges > 0 && integrityChecks > 1,
                    "Transaction campaign missed commit, rollback, reopen, schema, or integrity paths.");
            context.Metrics["documents"] = committed.Values.Sum(items => items.Count);
            context.Metrics["collections"] = committed.Count;
            context.Metrics["integrityChecks"] = integrityChecks;
            context.Metrics["commits"] = commits;
            context.Metrics["rollbacks"] = rollbacks;
            context.Metrics["reopens"] = reopens;
            context.Metrics["schemaChanges"] = schemaChanges;
        }
        finally { db.Dispose(); }
        return Task.CompletedTask;
    }

    private static Dictionary<string, SortedDictionary<int, BsonDocument>> NewModel(params string[] names) =>
        names.ToDictionary(name => name, _ => new SortedDictionary<int, BsonDocument>(), StringComparer.OrdinalIgnoreCase);

    private static void Insert(FuzzContext context, ILiteCollection<BsonDocument> collection,
        SortedDictionary<int, BsonDocument> model)
    {
        var id = context.Random.Next(1, 80);
        if (model.ContainsKey(id)) return;
        var document = Document(context.Random, id);
        collection.Insert(Clone(document));
        model[id] = document;
    }

    private static void Update(FuzzContext context, ILiteCollection<BsonDocument> collection,
        SortedDictionary<int, BsonDocument> model)
    {
        if (model.Count == 0) return;
        var id = model.Keys.ElementAt(context.Random.Next(model.Count));
        var document = Document(context.Random, id);
        context.Check(collection.Update(Clone(document)), "Update returned false for a modeled row.");
        model[id] = document;
    }

    private static void Upsert(FuzzContext context, ILiteCollection<BsonDocument> collection,
        SortedDictionary<int, BsonDocument> model)
    {
        var id = context.Random.Next(1, 80);
        var document = Document(context.Random, id);
        collection.Upsert(Clone(document));
        model[id] = document;
    }

    private static void Delete(FuzzContext context, ILiteCollection<BsonDocument> collection,
        SortedDictionary<int, BsonDocument> model)
    {
        var id = context.Random.Next(1, 80);
        var expected = model.Remove(id);
        context.Check(collection.Delete(id) == expected, "Delete result disagreed with the model.");
    }

    private static void InsertBulk(FuzzContext context, ILiteCollection<BsonDocument> collection,
        SortedDictionary<int, BsonDocument> model)
    {
        var documents = new List<BsonDocument>();
        for (var i = 0; i < 3; i++)
        {
            var id = context.Random.Next(80, 180);
            if (model.ContainsKey(id) || documents.Any(item => item["_id"].AsInt32 == id)) continue;
            documents.Add(Document(context.Random, id));
        }
        collection.InsertBulk(documents.Select(Clone));
        foreach (var document in documents) model[document["_id"].AsInt32] = document;
    }

    private static void DuplicateMustFail(FuzzContext context, ILiteCollection<BsonDocument> collection,
        SortedDictionary<int, BsonDocument> model)
    {
        if (model.Count == 0) return;
        var document = Clone(model.Values.First());
        Exception failure = null;
        try { collection.Insert(document); }
        catch (Exception error) { failure = error; }
        context.Check(failure is LiteException, "Duplicate key insert did not fail with LiteException.");
    }

    private static void Rename(FuzzContext context, LiteDatabase db,
        Dictionary<string, SortedDictionary<int, BsonDocument>> model, string oldName)
    {
        if (!db.CollectionExists(oldName)) return;
        var next = oldName.StartsWith("renamed", StringComparison.Ordinal) ? "collection" + context.Steps : "renamed" + context.Steps;
        context.Check(db.RenameCollection(oldName, next), "RenameCollection returned false.");
        var documents = model[oldName];
        model.Remove(oldName);
        model[next] = documents;
    }

    private static void DropAndCreate(FuzzContext context, LiteDatabase db,
        Dictionary<string, SortedDictionary<int, BsonDocument>> model, string name)
    {
        db.DropCollection(name);
        model[name] = new SortedDictionary<int, BsonDocument>();
        _ = db.GetCollection(name);
        context.Trace("drop-create", name);
    }

    private static void Validate(FuzzContext context, LiteDatabase db,
        Dictionary<string, SortedDictionary<int, BsonDocument>> model)
    {
        foreach (var pair in model)
        {
            var actual = db.GetCollection(pair.Key).Query().OrderBy("_id").ToArray();
            context.Check(actual.Length == pair.Value.Count, $"Count mismatch in {pair.Key}.");
            for (var i = 0; i < actual.Length; i++)
            {
                var expected = pair.Value.Values.ElementAt(i);
                context.Check(BsonSerializer.Serialize(actual[i]).SequenceEqual(BsonSerializer.Serialize(expected)),
                    $"Document mismatch in {pair.Key} for id {expected["_id"]}.");
            }
        }
    }

    private static BsonDocument Document(Random random, int id) => new()
    {
        ["_id"] = id, ["Value"] = random.Next(-1000, 1001), ["Text"] = new string((char)('a' + random.Next(26)), random.Next(0, 120)),
        ["Nested"] = new BsonDocument { ["Flag"] = random.Next(2) == 0, ["Numbers"] = new BsonArray(random.Next(), random.Next()) }
    };

    private static BsonDocument Clone(BsonDocument document) => BsonSerializer.Deserialize(BsonSerializer.Serialize(document));

    private static Dictionary<string, SortedDictionary<int, BsonDocument>> CloneModel(
        Dictionary<string, SortedDictionary<int, BsonDocument>> source) => source.ToDictionary(pair => pair.Key,
            pair => new SortedDictionary<int, BsonDocument>(pair.Value.ToDictionary(item => item.Key, item => Clone(item.Value))),
            StringComparer.OrdinalIgnoreCase);
}
