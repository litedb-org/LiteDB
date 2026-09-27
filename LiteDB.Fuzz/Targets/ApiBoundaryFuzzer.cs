namespace LiteDB.Fuzz.Targets;

internal sealed class ApiBoundaryFuzzer : IFuzzTarget
{
    public string Name => "api-boundary";
    public string Description => "Auto IDs, typed storage IDs, names, JSON, pragmas, system collections, and date boundaries.";

    public Task RunAsync(FuzzContext context)
    {
        var file = context.RegisterFile(Path.Combine(context.DirectoryPath, "api-boundary.db"));
        var cases = 0;
        while (context.Next())
        {
            using (var db = new LiteDatabase(file))
            {
                AutoIds(context, db);
                TypedStorage(context, db);
                Names(context, db);
                Json(context);
                PragmasAndDates(context, db);
                InvalidLocalDates(context);
                SystemCollections(context, db);
                db.Checkpoint();
            }
            using (var reopened = new LiteDatabase(file))
            {
                context.Check(reopened.UserVersion == context.Steps, "UserVersion did not persist across reopen.");
                context.Check(reopened.UtcDate == (context.Steps % 2 == 0), "UtcDate did not persist across reopen.");
                context.Check(reopened.LimitSize == 16L * 1024 * 1024 + context.Steps * Constants.PAGE_SIZE,
                    "LimitSize did not persist across reopen.");
            }
            DatabaseIntegrityVerifier.Verify(context, file);
            context.ObserveNovelty("api-boundary", context.Steps % 4, context.Steps % 3, context.Steps % 2);
            cases++;
        }
        context.Metrics["apiBoundaryCases"] = cases;
        return Task.CompletedTask;
    }

    private static void AutoIds(FuzzContext context, LiteDatabase db)
    {
        var modes = new[] { BsonAutoId.Int32, BsonAutoId.Int64, BsonAutoId.Guid, BsonAutoId.ObjectId };
        foreach (var mode in modes)
        {
            var name = "auto_" + mode;
            db.DropCollection(name);
            var rows = db.GetCollection(name, mode);
            db.BeginTrans();
            var rolledBack = rows.Insert(new BsonDocument { ["value"] = "rollback" });
            db.Rollback();
            var first = rows.Insert(new BsonDocument { ["value"] = "first" });
            var second = rows.Insert(new BsonDocument { ["value"] = "second" });
            context.Check(!first.IsNull && !second.IsNull && first != second,
                $"{mode} auto IDs were empty or duplicated after rollback.");
            context.Check(rows.FindById(rolledBack) == null, $"Rolled-back {mode} auto ID survived.");
        }
    }

    private static void TypedStorage(FuzzContext context, LiteDatabase db)
    {
        var storage = db.GetStorage<int>("typed_files", "typed_chunks");
        var id = context.Steps;
        var bytes = Enumerable.Range(0, 257 + context.Steps % 1024).Select(i => (byte)(i ^ id)).ToArray();
        using (var source = new MemoryStream(bytes)) storage.Upload(id, "typed.bin", source);
        using var output = new MemoryStream();
        storage.Download(id, output);
        context.Check(output.ToArray().SequenceEqual(bytes), "Typed FileStorage ID changed payload bytes.");
    }

    private static void Names(FuzzContext context, LiteDatabase db)
    {
        var names = new[] { "MiXeD", "under_score_" + context.Steps, new string('n', 60) + context.Steps % 10 };
        foreach (var name in names)
        {
            var rows = db.GetCollection(name);
            rows.Upsert(new BsonDocument { ["_id"] = context.Steps, ["name"] = name });
            context.Check(rows.FindById(context.Steps)?["name"] == name, $"Collection name {name} was not stable.");
        }
        Exception reserved = null;
        try { db.GetCollection("$fuzz").Insert(new BsonDocument { ["_id"] = 1 }); }
        catch (Exception error) { reserved = error; }
        context.Check(reserved is LiteException or ArgumentException,
            "Reserved system collection name was accepted or failed internally.");
        Exception unicode = null;
        try { db.GetCollection("é-集合").Insert(new BsonDocument { ["_id"] = 1 }); }
        catch (Exception error) { unicode = error; }
        context.Check(unicode is LiteException or ArgumentException,
            "Out-of-grammar collection name was accepted or failed internally.");
    }

    private static void Json(FuzzContext context)
    {
        var valid = $"{{ _id: {context.Steps}, text: 'é\\u0000', values: [1, null, true] }}";
        var document = JsonSerializer.Deserialize(valid).AsDocument;
        context.Check(document["_id"] == context.Steps && document["values"].AsArray.Count == 3,
            "Valid boundary JSON normalized incorrectly.");
        foreach (var malformed in new[] { "{", "[1,", "{x:'\\uZZZZ'}", "{x:1} trailing" })
        {
            Exception failure = null;
            try { JsonSerializer.Deserialize(malformed); }
            catch (Exception error) { failure = error; }
            context.Check(failure is LiteException or ArgumentException or FormatException,
                $"Malformed JSON `{malformed}` escaped through {failure?.GetType().FullName ?? "no exception"}.");
        }
    }

    private static void PragmasAndDates(FuzzContext context, LiteDatabase db)
    {
        db.UserVersion = context.Steps;
        db.UtcDate = context.Steps % 2 == 0;
        db.Timeout = TimeSpan.FromSeconds(1 + context.Steps % 60);
        db.CheckpointSize = context.Steps % 4;
        db.LimitSize = 16L * 1024 * 1024 + context.Steps * Constants.PAGE_SIZE;
        Exception invalidTimeout = null;
        try { db.Timeout = TimeSpan.Zero; }
        catch (Exception error) { invalidTimeout = error; }
        context.Check(invalidTimeout is LiteException or ArgumentException,
            "Invalid zero timeout was accepted or failed internally.");
        context.Check(db.GetCollectionNames().Any(), "Database was unusable after an invalid pragma.");
        var dates = db.GetCollection("dates");
        var value = new DateTime(2000 + context.Steps % 20, 1 + context.Steps % 12, 1 + context.Steps % 27,
            context.Steps % 24, context.Steps % 60, context.Steps % 60, DateTimeKind.Utc);
        dates.Upsert(new BsonDocument { ["_id"] = context.Steps, ["value"] = value });
        context.Check(dates.FindById(context.Steps)["value"].AsDateTime.ToUniversalTime() == value,
            "DateTime instant changed under UtcDate pragma.");
    }

    private static void InvalidLocalDates(FuzzContext context)
    {
        var twoOClock = new DateTime(1, 1, 1, 2, 0, 0);
        var start = TimeZoneInfo.TransitionTime.CreateFloatingDateRule(twoOClock, 4, 1, DayOfWeek.Sunday);
        var end = TimeZoneInfo.TransitionTime.CreateFloatingDateRule(twoOClock, 10, 5, DayOfWeek.Sunday);
        var rule = TimeZoneInfo.AdjustmentRule.CreateAdjustmentRule(DateTime.MinValue.Date,
            DateTime.MaxValue.Date, TimeSpan.FromHours(1), start, end);
        var zone = TimeZoneInfo.CreateCustomTimeZone("fuzz-dst", TimeSpan.FromHours(-5),
            "fuzz-dst", "standard", "daylight", new[] { rule });
        using var db = new LiteDatabase(new LiteDB.Engine.LiteEngine(new LiteDB.Engine.EngineSettings
        {
            DataStream = new MemoryStream(), RejectInvalidLocalTime = true, LocalTimeZone = zone
        }));
        var rows = db.GetCollection("dates");
        var gap = new DateTime(2006, 4, 2, 2, 30, 0, DateTimeKind.Unspecified);
        var invalid = Capture(() => rows.Insert(new BsonDocument { ["_id"] = 1, ["date"] = gap }));
        context.Check(invalid is ArgumentException && rows.Count() == 0,
            "Invalid local DateTime was accepted or partially published.");
        var ambiguous = new DateTime(2006, 10, 29, 1, 30, 0, DateTimeKind.Unspecified);
        rows.Insert(new BsonDocument { ["_id"] = 2, ["date"] = ambiguous });
        context.Check(rows.Count() == 1, "Ambiguous local DateTime was rejected or database stayed unusable.");
    }

    private static Exception Capture(Action action)
    {
        try { action(); return null; }
        catch (Exception error) { return error; }
    }

    private static void SystemCollections(FuzzContext context, LiteDatabase db)
    {
        foreach (var name in new[] { "$database", "$cols", "$indexes", "$sequences" })
        {
            Exception failure = null;
            try
            {
                using var reader = db.Execute($"SELECT $ FROM {name}");
                while (reader.Read()) { }
            }
            catch (Exception error) { failure = error; }
            context.Check(failure == null || failure is LiteException,
                $"System collection {name} failed through {failure?.GetType().FullName}.");
        }
    }
}
