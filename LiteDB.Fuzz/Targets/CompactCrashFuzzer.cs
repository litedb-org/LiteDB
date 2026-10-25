using LiteDB.Engine;

namespace LiteDB.Fuzz.Targets;

internal sealed class CompactCrashFuzzer : IFuzzTarget
{
    public string Name => "compact-crash";
    public string Description => "Torn compact promotion, schema/document WAL commits and checkpoints with full payload/index recovery.";

    public Task RunAsync(FuzzContext context)
    {
        while (context.Next())
        {
            var password = context.Random.Next(2) == 0 ? null : "compact-crash";
            var checkpoint = context.Random.Next(2) == 0;
            var count = context.Random.Next(4, 20);
            using var initialData = new MemoryStream();
            using var initialLog = new MemoryStream();
            using (var db = Open(initialData, initialLog, password, mode: CompactStorageMode.Legacy))
            {
                db.CheckpointSize = 0;
                db.GetCollection("rows").EnsureIndex("RepeatedPropertyName0");
                db.GetCollection("rows").Insert(Document(1));
                db.Checkpoint();
                db.GetCollection("other").Insert(new BsonDocument { ["_id"] = 1, ["payload"] = new string('k', 9000) });
            }
            using var device = new ChecksumCrashDevice(initialData.ToArray(), initialLog.ToArray(), context.Random);
            using (var db = Open(device.Data, device.Log, password))
            {
                db.CheckpointSize = 0;
                device.Armed = !checkpoint;
                db.GetCollection("rows").Insert(Enumerable.Range(2, count - 1).Select(Document));
                if (checkpoint)
                {
                    device.Armed = true;
                    db.Checkpoint();
                }
                device.Armed = false;
            }
            context.Check(device.SelectedData != null, "Compact scenario produced no crash events.");
            ChecksumFixture.Save(context, device.SelectedData, device.SelectedLog);
            Verify(context, device.SelectedData, device.SelectedLog, password, count, checkpoint);
            context.Trace("compact-crash", new
            {
                checkpoint, count, encrypted = password != null, device.SelectedEvent,
                device.SelectedPrefix, device.SelectedDurable, device.Events
            });
            context.ObserveNovelty("compact-crash", checkpoint, password != null, device.SelectedEvent, device.SelectedDurable);
        }
        return Task.CompletedTask;
    }

    private static void Verify(FuzzContext context, byte[] dataBytes, byte[] logBytes, string password, int count, bool committed)
    {
        using var data = ChecksumFixture.Copy(dataBytes);
        using var log = ChecksumFixture.Copy(logBytes);
        var recoveredCount = -1;
        foreach (var readOnly in new[] { true, false, true })
        {
            using (var db = Open(data, log, password, readOnly))
            {
                var rows = db.GetCollection("rows");
                var actual = rows.FindAll().OrderBy(row => row["_id"].AsInt32).ToArray();
                context.Check(actual.Length == count || (!committed && actual.Length == 1), "Recovered a partial compact transaction.");
                if (recoveredCount < 0) recoveredCount = actual.Length;
                context.Check(actual.Length == recoveredCount, "Checkpoint changed recovered compact transaction membership.");
                for (var id = 1; id <= actual.Length; id++)
                {
                    var expected = Document(id);
                    context.Check(ChecksumFixture.Equal(actual[id - 1], expected), "Recovered compact payload differs.");
                    var indexed = rows.Find(Query.EQ("RepeatedPropertyName0", id)).ToArray();
                    context.Check(indexed.Length == 1 && ChecksumFixture.Equal(indexed[0], expected), "Recovered compact index differs.");
                }
                context.Check(db.GetCollection("other").FindById(1)["payload"].AsString == new string('k', 9000),
                    "Compact promotion lost a previously acknowledged WAL document.");
                if (!readOnly) db.Checkpoint();
            }
            if (readOnly && recoveredCount >= 0 && dataBytes != null)
            {
                context.Check(data.ToArray().SequenceEqual(dataBytes) && log.ToArray().SequenceEqual(logBytes),
                    "Read-only compact recovery changed file bytes.");
            }
            if (!readOnly) { dataBytes = data.ToArray(); logBytes = log.ToArray(); }
        }
    }

    private static BsonDocument Document(int id)
    {
        var row = new BsonDocument { ["_id"] = id };
        for (var field = 0; field < 18; field++) row["RepeatedPropertyName" + field] = id + field;
        row["values"] = new BsonArray(Enumerable.Range(id, 60).Select(value => new BsonValue(value)));
        return row;
    }

    private static LiteDatabase Open(Stream data, Stream log, string password, bool readOnly = false,
        CompactStorageMode mode = CompactStorageMode.Compact) => new(new LiteEngine(new EngineSettings
        {
            DataStream = data, LogStream = log, Password = password, ReadOnly = readOnly,
            CompactStorage = mode, DurableCommits = true, TransactionPageLimit = 2
        }));
}
