using System.Security.Cryptography;

namespace LiteDB.Fuzz.Targets;

internal sealed class ReadOnlyFuzzer : IFuzzTarget
{
    public string Name => "read-only";
    public string Description => "Generated reads must leave data and WAL bytes unchanged, especially in read-only mode.";

    public Task RunAsync(FuzzContext context)
    {
        var file = context.RegisterFile(Path.Combine(context.DirectoryPath, "read-only.db"));
        using (var seed = new LiteDatabase(file))
        {
            var rows = seed.GetCollection("rows");
            rows.EnsureIndex("value", "Value");
            for (var id = 1; id <= 80; id++)
                rows.Insert(new BsonDocument { ["_id"] = id, ["Value"] = id % 13, ["Text"] = "row-" + id });
            seed.Checkpoint();
            seed.CheckpointSize = 0;
            rows.Insert(new BsonDocument { ["_id"] = 1000, ["Value"] = 7, ["Text"] = "dirty-wal" });
        }
        var log = Path.ChangeExtension(file, null) + "-log.db";
        context.Check(File.Exists(log) && new FileInfo(log).Length > 0,
            "Read-only campaign did not create a committed dirty WAL.");
        var baseline = Fingerprint(file, log);
        VerifyForbiddenMutations(context, file, log, baseline);
        var reads = 0;

        while (context.Next())
        {
            var readOnly = context.Steps % 2 != 0;
            using (var db = new LiteDatabase(new ConnectionString { Filename = file, ReadOnly = readOnly }))
            {
                var rows = db.GetCollection("rows");
                context.Check(rows.FindById(1000)?["Text"] == "dirty-wal",
                    "Read-only open did not expose the committed dirty WAL state.");
                var probe = context.Random.Next(13);
                rows.FindById(context.Random.Next(1, 81));
                rows.Count(BsonExpression.Create("Value = @value", new BsonDocument { ["value"] = probe }));
                rows.Query().Where("Value >= @0", probe).OrderBy("Value").Limit(7).ToArray();
                using (var reader = db.Execute("SELECT $._id FROM rows WHERE Value = @value ORDER BY _id",
                    new BsonDocument { ["value"] = probe }))
                    while (reader.Read()) { }
                using (var explain = db.Execute("EXPLAIN SELECT $ FROM rows WHERE Value = @value",
                    new BsonDocument { ["value"] = probe }))
                    while (explain.Read()) { }
            }
            context.Check(baseline == Fingerprint(file, log),
                $"A {(readOnly ? "read-only" : "read/write")} read workload changed persistent bytes.");
            context.ObserveNovelty("read-workload", readOnly, context.Steps % 5);
            reads++;
        }

        DatabaseIntegrityVerifier.Verify(context, file);
        context.Metrics["byteStableReadWorkloads"] = reads;
        return Task.CompletedTask;
    }

    private static void VerifyForbiddenMutations(FuzzContext context, string file, string log,
        string baseline)
    {
        var attempts = new Action<LiteDatabase>[]
        {
            db => db.GetCollection("rows").Insert(new BsonDocument { ["_id"] = 2001 }),
            db => db.GetCollection("rows").Update(new BsonDocument { ["_id"] = 1, ["Value"] = 999 }),
            db => db.GetCollection("rows").Delete(1),
            db => db.GetCollection("rows").EnsureIndex("forbidden", "Text"),
            db => db.GetCollection("rows").DropIndex("value"),
            db => db.DropCollection("rows"),
            db => db.RenameCollection("rows", "renamed"),
            db => db.UserVersion = 9,
            db => db.Rebuild(),
            db => db.FileStorage.Upload("x", "x", new MemoryStream(new byte[] { 1 }))
        };
        for (var index = 0; index < attempts.Length; index++)
        {
            Exception failure = null;
            using (var db = new LiteDatabase(new ConnectionString { Filename = file, ReadOnly = true }))
            {
                var before = LogicalState(db);
                try { attempts[index](db); }
                catch (Exception error) { failure = error; }
                var after = LogicalState(db);
                context.Check(before == after,
                    $"Forbidden read-only mutation {index} changed the visible logical state.");
            }
            context.Check(failure == null || failure is LiteException or InvalidOperationException or NotSupportedException or IOException or UnauthorizedAccessException,
                $"Forbidden read-only mutation {index} failed through {failure?.GetType().FullName}.");
            context.Check(Fingerprint(file, log) == baseline,
                $"Rejected read-only mutation {index} changed data or WAL bytes.");
        }
        context.Metrics["forbiddenReadOnlyMutations"] = attempts.Length;
    }

    private static string LogicalState(LiteDatabase db)
    {
        var rows = db.GetCollection("rows").Query().OrderBy("_id").ToArray();
        var documents = string.Join("|", rows.Select(row => Convert.ToBase64String(BsonSerializer.Serialize(row))));
        var names = string.Join("|", db.GetCollectionNames().OrderBy(name => name, StringComparer.Ordinal));
        var files = string.Join("|", db.FileStorage.FindAll().OrderBy(file => file.Id)
            .Select(file => $"{file.Id}:{file.Length}:{file.Filename}"));
        return $"{db.UserVersion};{db.UtcDate};{db.CheckpointSize};{names};{documents};{files}";
    }

    private static string Fingerprint(params string[] paths)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var path in paths)
        {
            var marker = BitConverter.GetBytes(File.Exists(path));
            hash.AppendData(marker);
            if (File.Exists(path))
            {
                hash.AppendData(BitConverter.GetBytes(new FileInfo(path).Length));
                using var stream = File.OpenRead(path);
                var buffer = new byte[81920];
                int read;
                while ((read = stream.Read(buffer, 0, buffer.Length)) != 0)
                    hash.AppendData(buffer, 0, read);
            }
        }
        return Convert.ToHexString(hash.GetHashAndReset());
    }
}
