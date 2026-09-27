namespace LiteDB.Fuzz.Targets;

internal sealed class IndexFuzzer : IFuzzTarget
{
    private static readonly BsonValue[] Keys =
    {
        BsonValue.Null, -1, 0, 1, 1L, 1d, 1m, int.MinValue, int.MaxValue,
        "", "a", "A", "é", "e\u0301", new string('z', 800)
    };

    public string Name => "index";
    public string Description => "Scalar/multikey/unique index mutations and key-moving updates checked against scans.";

    public Task RunAsync(FuzzContext context)
    {
        VerifyUniqueFailures(context);
        var file = context.RegisterFile(Path.Combine(context.DirectoryPath, "index.db"));
        var model = new SortedDictionary<int, BsonDocument>();
        var db = new LiteDatabase(new ConnectionString { Filename = file, TransactionPageLimit = 5, Collation = Collation.Binary });
        var integrityChecks = 0;
        var keyMovingUpdates = 0;
        try
        {
            var rows = db.GetCollection("rows");
            rows.EnsureIndex("value", "Value");
            rows.EnsureIndex("tags", "Tags[*]");
            rows.EnsureIndex("unique", "Unique", unique: true);
            while (context.Next())
            {
                var operation = context.Random.Next(7);
                var id = context.Random.Next(1, 100);
                if (operation <= 1 && !model.ContainsKey(id))
                {
                    var document = Document(context.Random, id);
                    rows.Insert(Clone(document));
                    model[id] = document;
                    context.Trace("insert", id);
                }
                else if (operation == 2 && model.ContainsKey(id))
                {
                    var document = Document(context.Random, id);
                    rows.Update(Clone(document));
                    model[id] = document;
                    context.Trace("update", id);
                }
                else if (operation == 3)
                {
                    rows.Delete(id);
                    model.Remove(id);
                    context.Trace("delete", id);
                }
                else if (operation == 4 && model.Count != 0)
                {
                    KeyMovingUpdate(context, rows, model);
                    keyMovingUpdates++;
                }
                else if (operation == 5)
                {
                    rows.DropIndex("tags");
                    rows.EnsureIndex("tags", "Tags[*]");
                    context.Trace("rebuild-multikey-index");
                }
                else if (operation == 6 && context.Steps % 11 == 0)
                {
                    db.Checkpoint();
                    db.Dispose();
                    db = new LiteDatabase(file);
                    rows = db.GetCollection("rows");
                    context.Trace("reopen");
                }
                Validate(context, rows, model);
                context.ObserveNovelty("index-state", operation, model.Count / 8,
                    keyMovingUpdates == 0 ? 0 : 1);
                if (context.Steps % 29 == 0)
                {
                    db.Checkpoint();
                    DatabaseIntegrityVerifier.Verify(context, file);
                    integrityChecks++;
                }
            }
            db.Checkpoint();
            DatabaseIntegrityVerifier.Verify(context, file);
            integrityChecks++;
            if (context.Steps >= 100)
                context.Check(keyMovingUpdates > 0 && integrityChecks > 1,
                    "Index campaign missed key-moving updates or periodic integrity walks.");
            context.Metrics["documents"] = model.Count;
            context.Metrics["keyMovingUpdates"] = keyMovingUpdates;
            context.Metrics["integrityChecks"] = integrityChecks;
        }
        finally { db.Dispose(); }
        return Task.CompletedTask;
    }

    private static void VerifyUniqueFailures(FuzzContext context)
    {
        var file = context.RegisterFile(Path.Combine(context.DirectoryPath, "unique-conflicts.db"));
        var connection = new ConnectionString { Filename = file, Collation = new Collation("en-US/IgnoreCase") };
        using (var db = new LiteDatabase(connection))
        {
            var rows = db.GetCollection("rows");
            rows.EnsureIndex("unique", "Unique", true);
            rows.Insert(Unique(1, "Alpha"));
            MustReject(context, () => rows.Insert(Unique(2, "Alpha")), "duplicate insert");
            MustReject(context, () => rows.Insert(Unique(2, "alpha")), "collation-equivalent insert");
            rows.Insert(Unique(2, "Beta"));
            MustReject(context, () => rows.Update(Unique(2, "ALPHA")), "key-moving update");
            context.Check(rows.FindById(2)["Unique"] == "Beta", "Failed unique update changed the document.");

            var beforeBulk = Snapshot(rows);
            MustReject(context, () => { rows.InsertBulk(new[]
            {
                Unique(3, "Gamma"), Unique(4, "Alpha"), Unique(5, "Delta")
            }); }, "mid-bulk conflict");
            context.Check(Snapshot(rows) == beforeBulk, "Mid-bulk unique failure partially published rows.");

            db.BeginTrans();
            MustReject(context, () => rows.Insert(Unique(6, "alpha")), "transactional conflict");
            db.Rollback();
            context.Check(Snapshot(rows) == beforeBulk, "Rollback after a unique failure changed committed state.");

            var conflicts = db.GetCollection("conflicts");
            conflicts.Insert(new[] { Unique(1, "same"), Unique(2, "SAME") });
            MustReject(context, () => conflicts.EnsureIndex("unique", "Unique", true),
                "unique index creation over conflicts");
            context.Check(!conflicts.DropIndex("unique"),
                "Failed unique-index creation left a partial index.");
            conflicts.Delete(2);
            context.Check(conflicts.EnsureIndex("unique", "Unique", true),
                "Collection was unusable after failed unique-index creation.");
            db.Checkpoint();
        }
        using (var reopened = new LiteDatabase(connection))
        {
            var rows = reopened.GetCollection("rows");
            context.Check(rows.Count() == 2 && rows.FindById(1)["Unique"] == "Alpha" &&
                rows.FindById(2)["Unique"] == "Beta", "Unique failures changed state after reopen.");
            rows.Insert(Unique(7, "usable"));
            rows.Delete(7);
            reopened.Checkpoint();
        }
        DatabaseIntegrityVerifier.Verify(context, file);
        context.Metrics["uniqueConflictScenarios"] = 6;
    }

    private static void MustReject(FuzzContext context, Action operation, string scenario)
    {
        Exception failure = null;
        try { operation(); }
        catch (Exception error) { failure = error; }
        context.Check(failure is LiteException,
            $"Unique {scenario} did not fail cleanly with LiteException.");
    }

    private static BsonDocument Unique(int id, string key) => new()
    {
        ["_id"] = id, ["Unique"] = key, ["Value"] = id
    };

    private static string Snapshot(ILiteCollection<BsonDocument> rows) => string.Join("|",
        rows.Query().OrderBy("_id").ToArray().Select(document =>
            Convert.ToBase64String(BsonSerializer.Serialize(document))));

    private static void KeyMovingUpdate(FuzzContext context, ILiteCollection<BsonDocument> rows,
        SortedDictionary<int, BsonDocument> model)
    {
        var chosen = model.Values.ElementAt(context.Random.Next(model.Count))["Value"];
        if (!chosen.IsNumber) return;
        var value = chosen.AsDouble;
        if (!double.IsFinite(value) || Math.Abs(value) > 1_000_000) return;
        var parameters = new BsonDocument { ["v"] = chosen, ["moved"] = value + 100d };
        var predicate = BsonExpression.Create("Value = @v OR Value = @moved", parameters);
        var matching = model.Values.Where(document =>
            document["Value"].CompareTo(parameters["v"], Collation.Binary) == 0 ||
            document["Value"].CompareTo(parameters["moved"], Collation.Binary) == 0).ToArray();
        var changed = rows.UpdateMany("{ Value: Value + 100, Hits: Hits + 1 }", predicate);
        context.Check(changed == matching.Length, "Key-moving UpdateMany revisited or skipped documents.");
        foreach (var document in matching)
        {
            document["Value"] = document["Value"].AsDouble + 100d;
            document["Hits"] = document["Hits"].AsInt32 + 1;
        }
        context.Trace("key-moving-update", new { value, changed });
    }

    private static void Validate(FuzzContext context, ILiteCollection<BsonDocument> rows,
        SortedDictionary<int, BsonDocument> model)
    {
        var all = rows.FindAll().ToArray();
        context.Check(all.Length == model.Count, "Index mutation count mismatch.");
        var probe = Keys[context.Random.Next(Keys.Length)];
        var parameters = new BsonDocument { ["key"] = probe };
        var scalar = BsonExpression.Create("Value = @key", parameters);
        var expectedScalar = model.Values.Where(document => document["Value"].CompareTo(probe, Collation.Binary) == 0)
            .Select(Id).OrderBy(x => x).ToArray();
        var actualScalar = rows.Query().Where(scalar).OrderBy("_id").ToArray().Select(Id).ToArray();
        Equal(context, expectedScalar, actualScalar, "scalar index");

        var multikey = BsonExpression.Create("Tags ANY = @key", parameters);
        var expectedMulti = model.Values.Where(document => document["Tags"].AsArray.Any(tag => tag.CompareTo(probe, Collation.Binary) == 0))
            .Select(Id).OrderBy(x => x).ToArray();
        var actualMulti = rows.Query().Where(multikey).OrderBy("_id").ToArray().Select(Id).ToArray();
        Equal(context, expectedMulti, actualMulti, "multikey index");

        var expectedOrder = model.Values.OrderBy(document => document["Value"], Comparer<BsonValue>.Create((a, b) => a.CompareTo(b, Collation.Binary)))
            .ThenBy(Id).Select(Id).ToArray();
        var actualOrder = rows.Query().OrderBy("Value").ThenBy("_id").ToArray().Select(Id).ToArray();
        Equal(context, expectedOrder, actualOrder, "index ordering");
        FuzzOracle.VerifyDistinct(context, actualOrder, EqualityComparer<int>.Default,
            "Index traversal returned duplicate documents.");
    }

    private static void Equal(FuzzContext context, int[] expected, int[] actual, string mode) =>
        FuzzOracle.VerifySequence(context, actual, expected,
            $"{mode} mismatch: expected [{string.Join(',', expected)}], actual [{string.Join(',', actual)}]");

    private static int Id(BsonDocument document) => document["_id"].AsInt32;

    private static BsonDocument Document(Random random, int id)
    {
        var first = Keys[random.Next(Keys.Length)];
        var second = random.Next(3) == 0 ? first : Keys[random.Next(Keys.Length)];
        return new BsonDocument
        {
            ["_id"] = id, ["Value"] = Keys[random.Next(Keys.Length)], ["Unique"] = $"u-{id:D4}",
            ["Tags"] = new BsonArray(first, second), ["Hits"] = 0
        };
    }

    private static BsonDocument Clone(BsonDocument document) => BsonSerializer.Deserialize(BsonSerializer.Serialize(document));
}
