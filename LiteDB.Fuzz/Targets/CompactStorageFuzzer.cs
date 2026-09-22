using LiteDB.Engine;

namespace LiteDB.Fuzz.Targets;

internal sealed class CompactStorageFuzzer : IFuzzTarget
{
    public string Name => "compact-storage";
    public string Description => "Auto/legacy promotion, mixed CRUD, rollback, reopen, rebuild, encryption, and raw integrity.";

    public Task RunAsync(FuzzContext context)
    {
        VerifyModeTransitions(context);

        var file = context.RegisterFile(Path.Combine(context.DirectoryPath, "compact-model.db"));
        var password = context.Seed % 2 == 0 ? $"compact-{context.Seed}" : null;
        var expected = new SortedDictionary<int, BsonDocument>();
        var reopens = 0;
        var rebuilds = 0;
        var rollbacks = 0;
        var db = Open(file, password);
        try
        {
            for (var id = 1; id <= 8; id++) Upsert(db, expected, CompactFuzzDocuments.Fixed(id));
            Verify(context, db, expected);

            while (context.Next())
            {
                var rows = db.GetCollection("rows");
                var id = context.Random.Next(1, 97);
                var shape = context.Random.Next(6);
                switch (context.Random.Next(10))
                {
                    case 0:
                    case 1:
                        Upsert(db, expected, CompactFuzzDocuments.Create(id, context.Random, shape));
                        break;
                    case 2:
                        rows.Delete(id);
                        expected.Remove(id);
                        break;
                    case 3:
                        db.BeginTrans();
                        rows.Upsert(CompactFuzzDocuments.Create(id, context.Random, shape));
                        rows.Delete(context.Random.Next(1, 97));
                        db.Rollback();
                        rollbacks++;
                        break;
                    case 4:
                        var committed = CompactFuzzDocuments.Create(id, context.Random, shape);
                        db.BeginTrans();
                        rows.Upsert(committed);
                        db.Commit();
                        expected[id] = CompactFuzzDocuments.Normalize(committed);
                        break;
                    case 5:
                        rows.EnsureIndex("kind", "$.Kind");
                        VerifyKindQuery(context, rows, expected, context.Random.Next(6));
                        break;
                    case 6:
                        rows.DropIndex("kind");
                        break;
                    case 7:
                        for (var offset = 0; offset < 3; offset++)
                        {
                            var bulkId = (id + offset - 1) % 96 + 1;
                            Upsert(db, expected, CompactFuzzDocuments.Create(bulkId, context.Random, shape));
                        }
                        break;
                    case 8:
                        db.Checkpoint();
                        db.Dispose();
                        db = Open(file, password);
                        reopens++;
                        break;
                    default:
                        db.Rebuild(new RebuildOptions { CompactStorage = CompactStorageMode.Auto });
                        rebuilds++;
                        break;
                }

                Verify(context, db, expected);
                context.ObserveNovelty("compact-storage", shape, expected.Count / 8,
                    reopens, rebuilds, rollbacks, password != null);
                context.Trace("compact-storage", new
                {
                    documents = expected.Count,
                    shape,
                    reopens,
                    rebuilds,
                    rollbacks,
                    encrypted = password != null
                });
            }

            db.Checkpoint();
        }
        finally
        {
            db.Dispose();
        }

        DatabaseIntegrityVerifier.Verify(context, file, password);
        using (var reopened = Open(file, password)) Verify(context, reopened, expected);
        context.Metrics["documents"] = expected.Count;
        context.Metrics["reopens"] = reopens;
        context.Metrics["rebuilds"] = rebuilds;
        context.Metrics["rollbacks"] = rollbacks;
        context.Metrics["encrypted"] = password != null;
        return Task.CompletedTask;
    }

    private static void VerifyModeTransitions(FuzzContext context)
    {
        var autoFile = context.RegisterFile(Path.Combine(context.DirectoryPath, "compact-auto.db"));
        using (var db = Open(autoFile))
        {
            db.GetCollection("docs").Insert(Enumerable.Range(1, 8).Select(CompactFuzzDocuments.Fixed));
            db.Checkpoint();
        }
        context.Check(ReadVersion(autoFile) == HeaderPage.COMPACT_FILE_VERSION,
            "Auto did not create a v10 database.");
        context.Check(HasSchemaPage(autoFile), "Auto did not persist a schema for repeated compact documents.");
        DatabaseIntegrityVerifier.Verify(context, autoFile);

        var legacyFile = context.RegisterFile(Path.Combine(context.DirectoryPath, "compact-legacy.db"));
        using (var db = Open(legacyFile, mode: CompactStorageMode.Legacy))
        {
            db.GetCollection("docs").Insert(Enumerable.Range(1, 4).Select(CompactFuzzDocuments.Fixed));
            db.Checkpoint();
        }
        context.Check(ReadVersion(legacyFile) == HeaderPage.FILE_VERSION,
            "Legacy mode did not retain v8.");
        using (var db = Open(legacyFile))
        {
            db.GetCollection("docs").Insert(Enumerable.Range(5, 8).Select(CompactFuzzDocuments.Fixed));
            db.Checkpoint();
        }
        context.Check(ReadVersion(legacyFile) == HeaderPage.COMPACT_FILE_VERSION,
            "Auto did not lazily promote an existing v8 database.");

        using (var db = Open(legacyFile))
            db.Rebuild(new RebuildOptions { CompactStorage = CompactStorageMode.Legacy });
        context.Check(ReadVersion(legacyFile) == HeaderPage.FILE_VERSION,
            "Legacy rebuild did not downgrade a compact database to v8.");
        using (var db = Open(legacyFile))
            db.Rebuild(new RebuildOptions { CompactStorage = CompactStorageMode.Auto });
        context.Check(ReadVersion(legacyFile) == HeaderPage.COMPACT_FILE_VERSION,
            "Auto rebuild did not promote a v8 database.");
        using (var db = Open(legacyFile))
            db.Rebuild(new RebuildOptions { CompactStorage = CompactStorageMode.Legacy });
        using (var db = Open(legacyFile))
        {
            db.GetCollection("docs").Insert(Enumerable.Range(20, 8).Select(CompactFuzzDocuments.Fixed));
            db.Checkpoint();
        }
        context.Check(ReadVersion(legacyFile) == HeaderPage.COMPACT_FILE_VERSION,
            "Auto did not re-promote a legacy rebuild.");
        DatabaseIntegrityVerifier.Verify(context, legacyFile);

        var rollbackFile = context.RegisterFile(Path.Combine(context.DirectoryPath, "compact-rollback.db"));
        using (var db = Open(rollbackFile, mode: CompactStorageMode.Legacy))
            db.GetCollection("docs").Insert(CompactFuzzDocuments.Fixed(1));
        using (var db = Open(rollbackFile))
        {
            db.BeginTrans();
            db.GetCollection("docs").Insert(Enumerable.Range(2, 8).Select(CompactFuzzDocuments.Fixed));
            db.Rollback();
        }
        context.Check(ReadVersion(rollbackFile) == HeaderPage.COMPACT_FILE_VERSION,
            "Rolled-back first compact write lost its durable v10 promotion.");
        using (var db = Open(rollbackFile))
            context.Check(db.GetCollection("docs").Count() == 1, "Rollback published compact documents or schemas.");
        DatabaseIntegrityVerifier.Verify(context, rollbackFile);
    }

    private static LiteDatabase Open(string file, string password = null,
        CompactStorageMode mode = CompactStorageMode.Auto) => new(new ConnectionString
    {
        Filename = file,
        Password = password,
        CompactStorage = mode,
        DurableCommits = true
    });

    private static void Upsert(LiteDatabase db, IDictionary<int, BsonDocument> expected, BsonDocument document)
    {
        db.GetCollection("rows").Upsert(document);
        expected[document["_id"].AsInt32] = CompactFuzzDocuments.Normalize(document);
    }

    private static void Verify(FuzzContext context, LiteDatabase db,
        IReadOnlyDictionary<int, BsonDocument> expected)
    {
        var actual = db.GetCollection("rows").FindAll().OrderBy(document => document["_id"].AsInt32).ToArray();
        context.Check(actual.Length == expected.Count, "Compact model document count differs from the database.");
        for (var i = 0; i < actual.Length; i++)
        {
            var pair = expected.ElementAt(i);
            context.Check(actual[i]["_id"].AsInt32 == pair.Key, "Compact model returned an unexpected ID.");
            context.Check(CompactFuzzDocuments.Equal(actual[i], pair.Value),
                $"Compact model document {pair.Key} differs after storage transition.");
        }
    }

    private static void VerifyKindQuery(FuzzContext context, ILiteCollection<BsonDocument> rows,
        IReadOnlyDictionary<int, BsonDocument> expected, int kind)
    {
        var actual = rows.Find(Query.EQ("$.Kind", kind)).Select(document => document["_id"].AsInt32).Order().ToArray();
        var modeled = expected.Where(pair => pair.Value["Kind"].AsInt32 == kind).Select(pair => pair.Key).Order().ToArray();
        context.Check(actual.SequenceEqual(modeled), "Compact secondary-index query differs from the model.");
    }

    private static byte ReadVersion(string file) => File.ReadAllBytes(file)[HeaderPage.P_FILE_VERSION];

    private static bool HasSchemaPage(string file)
    {
        var bytes = File.ReadAllBytes(file);
        return Enumerable.Range(0, bytes.Length / Constants.PAGE_SIZE)
            .Any(page => bytes[page * Constants.PAGE_SIZE + BasePage.P_PAGE_TYPE] == (byte)PageType.Schema);
    }
}
