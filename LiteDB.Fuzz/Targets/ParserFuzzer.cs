namespace LiteDB.Fuzz.Targets;

internal sealed class ParserFuzzer : IFuzzTarget
{
    private static readonly string[] Statements =
    {
        "SELECT $ FROM rows WHERE Score >= @min AND Score <= @max ORDER BY Score DESC, _id ASC LIMIT 9 OFFSET 2",
        "SELECT { key: @key, n: COUNT(*), total: SUM(*.Score) + @delta } FROM rows WHERE Score >= @min GROUP BY City HAVING COUNT(*) > @having ORDER BY key",
        "SELECT { alias: Score + @delta, name: Name } FROM rows WHERE Name LIKE @pattern ORDER BY alias DESC",
        "SELECT { values: ARRAY(MAP(Values => @ + @delta)) } FROM rows WHERE _id = @id",
        "SELECT { values: ARRAY(FILTER(Values => @ >= @min)) } FROM rows WHERE _id = @id",
        "EXPLAIN SELECT $ FROM rows WHERE (Score = @min OR Score = @max) AND Name != @name ORDER BY Score",
        "SELECT Ref FROM rows INCLUDE Ref WHERE _id = @id"
    };

    public string Name => "parser";
    public string Description => "Expression format/bind/cache and SQL template differential tests, including malformed mutations.";

    public Task RunAsync(FuzzContext context)
    {
        using var db = CreateDatabase();
        var sqlChecks = 0;
        var distinctStatements = new HashSet<string>(StringComparer.Ordinal);
        while (context.Next())
        {
            var parameters = Parameters(context.Random);
            var sql = context.Steps % 3 == 0
                ? Statements[Math.Abs((context.Seed + context.Steps) % Statements.Length)]
                : GeneratedStatement(context.Random);
            distinctStatements.Add(sql);
            var fresh = Read(db, sql, parameters, true);
            _ = Read(db, sql, parameters, false);
            var cached = Read(db, sql, parameters, false);
            Equal(context, fresh, cached, "SQL template", sql);
            sqlChecks++;

            var source = "(Score >= @min AND Score <= @max) OR Name = @name";
            var parsed = BsonExpression.Create(source, parameters);
            var reboundParameters = Parameters(context.Random);
            var rebound = parsed.Bind(reboundParameters);
            var direct = FreshExpression(source, reboundParameters);
            foreach (var row in db.GetCollection("rows").FindAll())
            {
                var actual = rebound.ExecuteScalar(row, Collation.Binary);
                var expected = direct.ExecuteScalar(row, Collation.Binary);
                context.Check(actual.Equals(expected), "BsonExpression.Bind differed from a fresh parse.");
            }
            var roundTrip = FreshExpression(parsed.Source, parameters);
            context.Check(roundTrip.Source == parsed.Source, "parse/format/parse did not preserve canonical source.");

            var left = BsonExpression.Create("Score >= @min", parameters);
            var right = BsonExpression.Create("Name = @name", parameters);
            var composed = context.Steps % 2 == 0 ? Query.And(left, right) : Query.Or(left, right);
            var boundComposition = composed.Bind(Clone(composed.Parameters));
            var rows = db.GetCollection("rows").FindAll().ToArray();
            var expectedCount = rows.Count(row =>
            {
                var l = left.ExecuteScalar(row, db.Collation).AsBoolean;
                var r = right.ExecuteScalar(row, db.Collation).AsBoolean;
                return context.Steps % 2 == 0 ? l && r : l || r;
            });
            var composedCount = db.GetCollection("rows").Count(boundComposition);
            context.Check(composedCount == expectedCount,
                $"Composed Query.And/Or count {composedCount} differed from independent Boolean evaluation {expectedCount}; source={boundComposition.Source}.");

            VerifyAbaAndInterleaving(context, db);
            if (context.Steps % 5 == 0) VerifyTerminalAndIntoSemantics(context, db);
            if (context.Steps % 7 == 0)
            {
                db.GetCollection("rows").DropIndex("score");
                var freshAfterDrop = Read(db, sql, parameters, true);
                Equal(context, freshAfterDrop, Read(db, sql, parameters, false), "SQL cache after index drop", sql);
                db.GetCollection("rows").EnsureIndex("score", "Score");
                var freshAfterCreate = Read(db, sql, parameters, true);
                Equal(context, freshAfterCreate, Read(db, sql, parameters, false), "SQL cache after index create", sql);
            }

            var malformed = source.Insert(context.Random.Next(source.Length), new[] { "(", "@", "'", "]", " => " }[context.Random.Next(5)]);
            var cachedError = Error(() => BsonExpression.Create(malformed, parameters));
            var freshError = Error(() => FreshExpression(malformed, parameters));
            context.Check(cachedError?.GetType() == freshError?.GetType(),
                $"Malformed expression error mismatch: cached={cachedError?.GetType()}, fresh={freshError?.GetType()}");
            _ = Read(db, "SELECT $._id FROM rows WHERE Score = @value", new BsonDocument { ["value"] = context.Steps % 17 }, false);
            context.Trace("parser", new { sql, expression = source, malformed });
        }
        if (context.Steps >= 30)
            context.Check(distinctStatements.Count >= 15, "Parser campaign did not reach enough distinct SQL grammar shapes.");
        context.Metrics["sqlDifferentialChecks"] = sqlChecks;
        context.Metrics["distinctStatements"] = distinctStatements.Count;
        return Task.CompletedTask;
    }

    private static LiteDatabase CreateDatabase()
    {
        var db = new LiteDatabase(":memory:");
        var rows = db.GetCollection("rows");
        rows.InsertBulk(Enumerable.Range(1, 60).Select(id => new BsonDocument
        {
            ["_id"] = id, ["Score"] = id % 17, ["City"] = "city" + id % 4,
            ["Name"] = "name" + id, ["Values"] = new BsonArray(id, id + 1, id + 2),
            ["Ref"] = new BsonDocument { ["$id"] = id, ["$ref"] = "related" }
        }));
        db.GetCollection("related").InsertBulk(Enumerable.Range(1, 60).Select(id =>
            new BsonDocument { ["_id"] = id, ["Value"] = "related" + id }));
        rows.EnsureIndex("score", "Score");
        rows.EnsureIndex("name", "Name");
        return db;
    }

    private static BsonDocument Parameters(Random random) => new()
    {
        ["min"] = random.Next(0, 12), ["max"] = random.Next(12, 24), ["limit"] = random.Next(1, 12),
        ["offset"] = random.Next(0, 5), ["delta"] = random.Next(-5, 6), ["having"] = random.Next(0, 5),
        ["id"] = random.Next(1, 61), ["name"] = "name" + random.Next(1, 61), ["pattern"] = "%" + random.Next(10) + "%"
    };

    private static string GeneratedStatement(Random random)
    {
        var field = new[] { "Score", "_id", "Name" }[random.Next(3)];
        var predicate = field == "Name"
            ? $"{field} {new[] { "=", "!=", "LIKE" }[random.Next(3)]} {(random.Next(3) == 2 ? "@pattern" : "@name")}"
            : $"{field} {new[] { "=", "!=", ">", ">=", "<", "<=" }[random.Next(6)]} @min";
        if (random.Next(2) == 0)
            predicate = $"({predicate}) {new[] { "AND", "OR" }[random.Next(2)]} Score <= @max";
        var projection = random.Next(3) switch
        {
            0 => "$",
            1 => "{ alias: Score + @delta, name: Name, city: City }",
            _ => "{ id: _id, values: ARRAY(MAP(Values => @ + @delta)) }"
        };
        var order = projection.StartsWith("{ alias", StringComparison.Ordinal)
            ? "alias" : new[] { "_id", "Score", "Name" }[random.Next(3)];
        var direction = random.Next(2) == 0 ? "ASC" : "DESC";
        return $"SELECT {projection} FROM rows WHERE {predicate} ORDER BY {order} {direction} " +
            $"LIMIT {random.Next(1, 15)} OFFSET {random.Next(0, 5)}";
    }

    private static BsonValue[] Read(LiteDatabase db, string sql, BsonDocument parameters, bool fresh)
    {
        using var reader = fresh ? db.Execute(new StringReader(sql), parameters) : db.Execute(sql, parameters);
        var values = new List<BsonValue>();
        while (reader.Read()) values.Add(reader.Current);
        return values.ToArray();
    }

    private static BsonExpression FreshExpression(string source, BsonDocument parameters)
    {
        var previous = BsonExpression.DisableCompilationCache;
        BsonExpression.DisableCompilationCache = true;
        try { return BsonExpression.Create(source, parameters); }
        finally { BsonExpression.DisableCompilationCache = previous; }
    }

    private static Exception Error(Action action)
    {
        try { action(); return null; }
        catch (Exception error) { return error; }
    }

    private static BsonDocument Clone(BsonDocument document) =>
        BsonSerializer.Deserialize(BsonSerializer.Serialize(document));

    private static void VerifyAbaAndInterleaving(FuzzContext context, LiteDatabase db)
    {
        const string statement = "SELECT $._id FROM rows WHERE Score = @value ORDER BY _id";
        var a = context.Steps % 4 switch
        {
            0 => new BsonValue(context.Steps % 17),
            1 => new BsonValue((long)(context.Steps % 17)),
            2 => BsonValue.Null,
            _ => new BsonValue((double)(context.Steps % 17))
        };
        var b = new BsonValue((context.Steps + 3) % 17);
        var first = Read(db, statement, new BsonDocument { ["value"] = a }, false);
        using (var readerA = db.Execute(statement, new BsonDocument { ["value"] = a }))
        {
            var interleaved = new List<BsonValue>();
            if (readerA.Read()) interleaved.Add(readerA.Current);
            _ = Read(db, statement, new BsonDocument { ["value"] = b }, false);
            while (readerA.Read()) interleaved.Add(readerA.Current);
            Equal(context, first, interleaved.ToArray(), "interleaved SQL reader", statement);
        }
        Equal(context, first, Read(db, statement, new BsonDocument { ["value"] = a }, false),
            "typed/null SQL A-B-A", statement);
    }

    private static void VerifyTerminalAndIntoSemantics(FuzzContext context, LiteDatabase db)
    {
        var rows = db.GetCollection("rows");
        var score = context.Steps % 17;
        var expression = BsonExpression.Create("Score = @score", new BsonDocument { ["score"] = score });
        var expected = rows.FindAll().Where(row => row["Score"] == score)
            .OrderBy(row => row["_id"]).ToArray();
        var query = rows.Query().Where(expression).OrderBy("_id");
        context.Check(query.Count() == expected.Length && query.LongCount() == expected.LongLength &&
            query.Exists() == (expected.Length != 0), "Count/LongCount/Exists disagreed with the model.");
        context.Check(query.FirstOrDefault()?["_id"] == expected.FirstOrDefault()?["_id"],
            "FirstOrDefault disagreed with the model.");

        var id = 1 + context.Steps % 60;
        var single = rows.Query().Where(BsonExpression.Create("_id = @id", new BsonDocument { ["id"] = id }));
        context.Check(single.Single()["_id"] == id && single.SingleOrDefault()["_id"] == id,
            "Single/SingleOrDefault disagreed for a unique predicate.");
        context.Check(rows.Query().Where("_id = @0", id).ForUpdate().Single()["_id"] == id,
            "ForUpdate changed a unique query result.");

        const string target = "cache_into";
        db.DropCollection(target);
        var copied = rows.Query().Where(expression).Into(target, BsonAutoId.Int32);
        context.Check(copied == expected.Length, "Into returned the wrong copied-row count.");
        var actual = db.GetCollection(target).Query().OrderBy("_id").ToArray();
        context.Check(actual.Length == expected.Length && actual.Zip(expected,
            (left, right) => BsonSerializer.Serialize(left).SequenceEqual(BsonSerializer.Serialize(right))).All(equal => equal),
            "Into changed projected documents.");
        db.DropCollection(target);
    }

    private static void Equal(FuzzContext context, BsonValue[] expected, BsonValue[] actual, string mode, string source)
    {
        context.Check(expected.Length == actual.Length, $"{mode} row count mismatch for {source}.");
        for (var i = 0; i < expected.Length; i++)
        {
            var expectedBytes = BsonSerializer.Serialize(expected[i].IsDocument ? expected[i].AsDocument : new BsonDocument { ["v"] = expected[i] });
            var actualBytes = BsonSerializer.Serialize(actual[i].IsDocument ? actual[i].AsDocument : new BsonDocument { ["v"] = actual[i] });
            context.Check(expectedBytes.SequenceEqual(actualBytes), $"{mode} value mismatch at row {i} for {source}.");
        }
    }
}
