using LiteDB.Engine;

namespace LiteDB.Fuzz.Targets;

internal sealed class RebuildFuzzer : IFuzzTarget
{
    public string Name => "rebuild";
    public string Description => "Logical-state preservation across rebuild, collation/password changes, reopen, and corruption probes.";

    public Task RunAsync(FuzzContext context)
    {
        var file = context.RegisterFile(Path.Combine(context.DirectoryPath, "rebuild.db"));
        string password = null;
        var expected = new SortedDictionary<int, BsonDocument>();
        var db = Open(file, password);
        var corruptionProbes = 0;
        var corruptionRejections = 0;
        var integrityChecks = 0;
        try
        {
            while (context.Next())
            {
                Mutate(context, db.GetCollection("rows"), expected);
                db.GetCollection("rows").EnsureIndex("value", "Value");
                var options = Options(context, password, out var nextPassword);
                Verify(context, db, expected);
                var reclaimed = db.Rebuild(options);
                context.Trace("rebuild", new { reclaimed, passwordChanged = password != nextPassword, collation = options.Collation?.ToString() });
                password = nextPassword;
                Verify(context, db, expected);
                db.Dispose();
                db = Open(file, password);
                Verify(context, db, expected);
                db.Checkpoint();
                DatabaseIntegrityVerifier.Verify(context, file, password);
                integrityChecks++;
                DeleteSuccessfulBackups(context.DirectoryPath);

                if (context.Steps % 7 == 0)
                {
                    var corrupt = context.RegisterFile(Path.Combine(context.DirectoryPath, "corrupt-probe.db"));
                    File.Copy(file, corrupt, true);
                    Corrupt(corrupt, context.Random);
                    corruptionProbes++;
                    if (ProbeCorruption(context, corrupt, password, expected)) corruptionRejections++;
                    Verify(context, db, expected);
                }
            }
            if (context.Steps >= 35)
                context.Check(corruptionProbes >= 5 && integrityChecks > 0,
                    "Rebuild campaign missed corruption probes or raw integrity verification.");
            context.Metrics["documents"] = expected.Count;
            context.Metrics["encrypted"] = password != null;
            context.Metrics["corruptionProbes"] = corruptionProbes;
            context.Metrics["corruptionRejections"] = corruptionRejections;
            context.Metrics["integrityChecks"] = integrityChecks;
        }
        finally { db.Dispose(); }
        return Task.CompletedTask;
    }

    private static LiteDatabase Open(string file, string password) => new(new ConnectionString
    {
        Filename = file, Password = password, DurableCommits = true
    });

    private static void Mutate(FuzzContext context, ILiteCollection<BsonDocument> rows,
        SortedDictionary<int, BsonDocument> expected)
    {
        for (var i = 0; i < 4; i++)
        {
            var id = context.Random.Next(1, 120);
            if (context.Random.Next(5) == 0)
            {
                rows.Delete(id);
                expected.Remove(id);
            }
            else
            {
                var document = new BsonDocument
                {
                    ["_id"] = id, ["Value"] = context.Random.Next(-10000, 10001),
                    ["Text"] = new string((char)('a' + context.Random.Next(26)), context.Random.Next(0, 700)),
                    ["Array"] = new BsonArray(context.Random.Next(), context.Random.Next(), context.Random.Next())
                };
                rows.Upsert(Clone(document));
                expected[id] = document;
            }
        }
    }

    private static RebuildOptions Options(FuzzContext context, string password, out string nextPassword)
    {
        var options = new RebuildOptions
        {
            Collation = context.Steps % 2 == 0 ? Collation.Binary : new Collation("en-US/IgnoreCase"),
            IncludeErrorReport = true
        };
        if (context.Steps % 5 == 0 && password != null)
        {
            options.RemovePassword = true;
            nextPassword = null;
        }
        else if (context.Steps % 5 == 1)
        {
            nextPassword = "p-" + context.Seed + "-" + context.Steps;
            options.Password = nextPassword;
        }
        else nextPassword = password;
        return options;
    }

    private static void Verify(FuzzContext context, LiteDatabase db, SortedDictionary<int, BsonDocument> expected)
    {
        var actual = db.GetCollection("rows").Query().OrderBy("_id").ToArray();
        context.Check(actual.Length == expected.Count, "Rebuild changed the document count.");
        for (var i = 0; i < expected.Count; i++)
            context.Check(BsonSerializer.Serialize(actual[i]).SequenceEqual(BsonSerializer.Serialize(expected.Values.ElementAt(i))),
                $"Rebuild changed document at ordinal {i}.");
        var byValue = db.GetCollection("rows").Query().OrderBy("Value").ThenBy("_id").ToArray();
        var ordered = expected.Values.OrderBy(document => document["Value"]).ThenBy(document => document["_id"]).ToArray();
        for (var i = 0; i < ordered.Length; i++)
            context.Check(byValue[i]["_id"] == ordered[i]["_id"], "Rebuilt secondary-index order differs from the model.");
    }

    private static void Corrupt(string file, Random random)
    {
        using var stream = new FileStream(file, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        if (stream.Length <= Constants.PAGE_SIZE) return;
        var position = random.NextInt64(Constants.PAGE_SIZE, stream.Length);
        stream.Position = position;
        var value = stream.ReadByte();
        stream.Position = position;
        stream.WriteByte((byte)(value ^ 0x5a));
        stream.Flush(true);
    }

    private static bool ProbeCorruption(FuzzContext context, string file, string password,
        SortedDictionary<int, BsonDocument> expected)
    {
        try
        {
            using var probe = Open(file, password);
            Verify(context, probe, expected);
            probe.Checkpoint();
            DatabaseIntegrityVerifier.Verify(context, file, password);
            return false;
        }
        catch (Exception error) when (error is LiteException or IOException or InvalidOperationException
            or System.Text.DecoderFallbackException)
        {
            return true;
        }
    }

    private static void DeleteSuccessfulBackups(string directory)
    {
        foreach (var backup in Directory.EnumerateFiles(directory, "rebuild-backup*.db"))
            File.Delete(backup);
    }

    private static BsonDocument Clone(BsonDocument document) => BsonSerializer.Deserialize(BsonSerializer.Serialize(document));
}
