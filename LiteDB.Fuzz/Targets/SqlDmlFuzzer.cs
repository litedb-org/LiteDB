namespace LiteDB.Fuzz.Targets;

internal sealed class SqlDmlFuzzer : IFuzzTarget
{
    public string Name => "sql-dml";
    public string Description => "SQL DML/DDL/transactions/pragmas differential against equivalent C# API state.";

    public Task RunAsync(FuzzContext context)
    {
        using var api = new LiteDatabase(new MemoryStream());
        using var sql = new LiteDatabase(new MemoryStream());
        var collection = "rows";
        var transaction = false;
        var mutations = 0;
        while (context.Next())
        {
            var left = api.GetCollection(collection);
            var operation = context.Random.Next(14);
            switch (operation)
            {
                case 0:
                case 1:
                    var id = context.Random.Next(1, 100);
                    if (left.FindById(id) != null) break;
                    var document = Document(context.Random, id);
                    left.Insert(Clone(document));
                    Execute(sql, $"INSERT INTO {collection} VALUES {JsonSerializer.Serialize(document)}");
                    mutations++;
                    break;
                case 2:
                case 3:
                    MutateMany(context, left, sql, collection);
                    mutations++;
                    break;
                case 4:
                    var threshold = context.Random.Next(-20, 21);
                    var predicate = BsonExpression.Create("Value < @v OR Tags ANY = @tag",
                        new BsonDocument { ["v"] = threshold, ["tag"] = threshold % 5 });
                    var apiDeleted = left.DeleteMany(predicate);
                    var sqlDeleted = ExecuteInt(sql, $"DELETE {collection} WHERE Value < @v OR Tags ANY = @tag",
                        new BsonDocument { ["v"] = threshold, ["tag"] = threshold % 5 });
                    context.Check(apiDeleted == sqlDeleted, "SQL DELETE count differed from API DeleteMany.");
                    mutations++;
                    break;
                case 5:
                    var createdApi = left.EnsureIndex("value", "Value");
                    var createdSql = ExecuteBool(sql, $"CREATE INDEX value ON {collection} (Value)");
                    context.Check(createdApi == createdSql, "SQL CREATE INDEX differed from API.");
                    break;
                case 6:
                    var droppedApi = left.DropIndex("value");
                    var droppedSql = ExecuteBool(sql, $"DROP INDEX {collection}.value");
                    context.Check(droppedApi == droppedSql, "SQL DROP INDEX differed from API.");
                    break;
                case 7 when !transaction:
                    context.Check(api.BeginTrans(), "API BEGIN failed.");
                    Execute(sql, "BEGIN");
                    transaction = true;
                    break;
                case 8 when transaction:
                    if (context.Random.Next(2) == 0)
                    {
                        context.Check(api.Commit(), "API COMMIT failed.");
                        Execute(sql, "COMMIT");
                    }
                    else
                    {
                        context.Check(api.Rollback(), "API ROLLBACK failed.");
                        Execute(sql, "ROLLBACK");
                    }
                    transaction = false;
                    break;
                case 9 when !transaction:
                    ApplyPragmas(context, api, sql);
                    break;
                case 10 when !transaction:
                    api.Checkpoint();
                    Execute(sql, "CHECKPOINT");
                    break;
                case 11 when !transaction && context.Steps % 7 == 0:
                    api.Rebuild();
                    Execute(sql, "REBUILD");
                    break;
                case 12 when !transaction:
                    var apiRenamed = api.RenameCollection(collection, "renamed_rows");
                    var sqlRenamed = ExecuteBool(sql, $"RENAME COLLECTION {collection} TO renamed_rows");
                    context.Check(apiRenamed == sqlRenamed, "SQL RENAME COLLECTION differed from API.");
                    if (apiRenamed)
                    {
                        context.Check(api.RenameCollection("renamed_rows", collection), "API rename-back failed.");
                        context.Check(ExecuteBool(sql, $"RENAME COLLECTION renamed_rows TO {collection}"),
                            "SQL rename-back failed.");
                    }
                    break;
                case 13 when !transaction:
                    var apiDropped = api.DropCollection(collection);
                    var sqlDropped = ExecuteBool(sql, $"DROP COLLECTION {collection}");
                    context.Check(apiDropped == sqlDropped, "SQL DROP COLLECTION differed from API.");
                    break;
            }
            Compare(context, api, sql, collection);
            QueryReuse(context, sql, collection);
            context.ObserveNovelty("sql-dml", operation, transaction, left.Count() / 8);
        }
        if (transaction)
        {
            api.Rollback();
            Execute(sql, "ROLLBACK");
        }
        Compare(context, api, sql, collection);
        context.Metrics["sqlDmlMutations"] = mutations;
        return Task.CompletedTask;
    }

    private static void MutateMany(FuzzContext context, ILiteCollection<BsonDocument> api,
        LiteDatabase sql, string collection)
    {
        var pivot = context.Random.Next(-25, 26);
        var shape = context.Steps % 4;
        var predicate = shape switch
        {
            0 => "Value >= @p AND Value <= @p + 4",
            1 => "Value = @p OR Value = @p + 1",
            2 => "Value IN [@p, @p + 1, @p + 2]",
            _ => "Tags ANY = @tag"
        };
        var parameters = new BsonDocument { ["p"] = pivot, ["tag"] = Math.Abs(pivot % 5) };
        var transform = shape % 2 == 0
            ? "{ Value: Value + 17, Hits: Hits + 1 }"
            : "{ Value: Value - 11, Tags: ARRAY([@tag, Value]), Hits: Hits + 1 }";
        var apiCount = api.UpdateMany(BsonExpression.Create(transform, parameters),
            BsonExpression.Create(predicate, parameters));
        var sqlCount = ExecuteInt(sql, $"UPDATE {collection} SET {transform} WHERE {predicate}", parameters);
        context.Check(apiCount == sqlCount, "SQL UPDATE count differed from API UpdateMany.");
    }

    private static void ApplyPragmas(FuzzContext context, LiteDatabase api, LiteDatabase sql)
    {
        var version = context.Random.Next(0, 1000);
        api.UserVersion = version;
        Execute(sql, $"PRAGMA USER_VERSION = {version}");
        var checkpoint = context.Random.Next(1, 4);
        api.CheckpointSize = checkpoint;
        Execute(sql, $"PRAGMA CHECKPOINT = {checkpoint}");
        var utc = context.Random.Next(2) == 0;
        api.UtcDate = utc;
        Execute(sql, $"PRAGMA UTC_DATE = {utc.ToString().ToLowerInvariant()}");
        context.Check(api.UserVersion == sql.UserVersion && api.CheckpointSize == sql.CheckpointSize &&
            api.UtcDate == sql.UtcDate, "SQL pragma state differed from API state.");
    }

    private static void QueryReuse(FuzzContext context, LiteDatabase db, string collection)
    {
        const string query = "SELECT { n: COUNT(*), total: SUM(*.Value) } FROM rows WHERE Value >= @v";
        var a = new BsonDocument { ["v"] = -5 };
        var b = new BsonDocument { ["v"] = 5 };
        var first = Read(db, query.Replace("rows", collection), a);
        _ = Read(db, query.Replace("rows", collection), b);
        var again = Read(db, query.Replace("rows", collection), a);
        context.Check(first == again, "SQL cache A-B-A reuse retained the wrong parameters.");
    }

    private static void Compare(FuzzContext context, LiteDatabase api, LiteDatabase sql, string name)
    {
        var left = Snapshot(api.GetCollection(name));
        var right = Snapshot(sql.GetCollection(name));
        context.Check(left == right, "SQL and API logical states diverged.");
    }

    private static string Snapshot(ILiteCollection<BsonDocument> rows) => string.Join("|",
        rows.Query().OrderBy("_id").ToArray().Select(document =>
            Convert.ToBase64String(BsonSerializer.Serialize(document))));

    private static string Read(LiteDatabase db, string statement, BsonDocument parameters)
    {
        using var reader = db.Execute(statement, parameters);
        var values = new List<string>();
        while (reader.Read()) values.Add(reader.Current.ToString());
        return string.Join("|", values);
    }

    private static void Execute(LiteDatabase db, string statement, BsonDocument parameters = null)
    { using var reader = db.Execute(statement, parameters ?? new BsonDocument()); while (reader.Read()) { } }

    private static int ExecuteInt(LiteDatabase db, string statement, BsonDocument parameters = null)
    { using var reader = db.Execute(statement, parameters ?? new BsonDocument()); return reader.Current.AsInt32; }

    private static bool ExecuteBool(LiteDatabase db, string statement)
    { using var reader = db.Execute(statement); return reader.Current.AsBoolean; }

    private static BsonDocument Document(Random random, int id) => new()
    {
        ["_id"] = id, ["Value"] = random.Next(-30, 31), ["Hits"] = 0,
        ["Tags"] = new BsonArray(random.Next(5), random.Next(5)), ["Text"] = "row-" + id
    };

    private static BsonDocument Clone(BsonDocument document) =>
        BsonSerializer.Deserialize(BsonSerializer.Serialize(document));
}
