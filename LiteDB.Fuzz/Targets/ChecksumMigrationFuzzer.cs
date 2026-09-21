using LiteDB.Engine;

namespace LiteDB.Fuzz.Targets;

internal sealed class ChecksumMigrationFuzzer : IFuzzTarget
{
    public string Name => "checksum-migration";
    public string Description => "v8/v9 dirty-WAL cutover, mixed-page CRUD/rollback/reuse and independent CRC/model audits.";

    public Task RunAsync(FuzzContext context)
    {
        while (context.Next())
        {
            using var fixture = new ChecksumFixture(context);
            var version = (byte)(8 + context.Random.Next(2));
            var dirty = context.Random.Next(2) == 0;
            if (dirty)
            {
                using var legacyWriter = ChecksumFixture.Open(fixture.Data, fixture.Log, fixture.Password);
                legacyWriter.CheckpointSize = 0;
                var added = ChecksumFixture.Document(context.Random, 100);
                legacyWriter.GetCollection("rows").Insert(ChecksumFixture.Clone(added));
                fixture.Rows[100] = added;
            }
            fixture.MakeLegacy(version);
            var before = fixture.Data.ToArray();
            var beforeLog = fixture.Log.ToArray();
            ChecksumFixture.Save(context, before, beforeLog);
            using (var reader = ChecksumFixture.Open(fixture.Data, fixture.Log, fixture.Password, true))
                ChecksumFixture.Verify(context, reader, fixture.Rows, fixture.Cold);
            context.Check(before.SequenceEqual(fixture.Data.ToArray()) && beforeLog.SequenceEqual(fixture.Log.ToArray()),
                "Read-only legacy recovery changed physical bytes.");
            using (var db = ChecksumFixture.Open(fixture.Data, fixture.Log, fixture.Password))
            {
                db.CheckpointSize = 0;
                ChecksumFixture.Verify(context, db, fixture.Rows, fixture.Cold);
                var info = db.GetCollection("$database").FindAll().Single();
                context.Check(info["checksumCoverage"].AsString == "Mixed", "Cutover did not publish Mixed coverage.");
                var skip = fixture.Password == null ? 8192 : 16384;
                if (!dirty) context.Check(before.Skip(skip).SequenceEqual(fixture.Data.ToArray().Skip(skip)),
                    "Clean cutover rewrote non-header pages.");
                var boundary = info["legacyLastPageID"].AsInt64;
                for (var batch = 0; batch < 8; batch++)
                {
                    db.BeginTrans();
                    var candidate = new SortedDictionary<int, BsonDocument>(fixture.Rows);
                    var operations = context.Random.Next(1, 8);
                    for (var i = 0; i < operations; i++)
                    {
                        var id = context.Random.Next(1, 40);
                        if (context.Random.Next(4) == 0)
                        {
                            db.GetCollection("rows").Delete(id);
                            candidate.Remove(id);
                        }
                        else
                        {
                            var row = ChecksumFixture.Document(context.Random, id);
                            db.GetCollection("rows").Upsert(ChecksumFixture.Clone(row));
                            candidate[id] = row;
                        }
                    }
                    var rollback = context.Random.Next(3) == 0;
                    if (rollback) db.Rollback();
                    else
                    {
                        db.Commit();
                        fixture.Rows.Clear();
                        foreach (var pair in candidate) fixture.Rows.Add(pair.Key, pair.Value);
                    }
                    db.Checkpoint();
                    ChecksumFixture.Save(context, fixture.Data.ToArray(), fixture.Log.ToArray());
                    ChecksumFixture.Verify(context, db, fixture.Rows, fixture.Cold);
                    ChecksumFixture.Audit(context, fixture.Data, fixture.Password);
                    context.Check(db.GetCollection("$database").FindAll().Single()["legacyLastPageID"].AsInt64 == boundary,
                        "Ordinary writes moved the fixed legacy boundary.");
                    context.Trace("migration-batch", new { version, dirty, encrypted = fixture.Password != null, batch, operations, rollback });
                }
            }
            before = fixture.Data.ToArray();
            beforeLog = fixture.Log.ToArray();
            using (var reader = ChecksumFixture.Open(fixture.Data, fixture.Log, fixture.Password, true))
                ChecksumFixture.Verify(context, reader, fixture.Rows, fixture.Cold);
            context.Check(before.SequenceEqual(fixture.Data.ToArray()) && beforeLog.SequenceEqual(fixture.Log.ToArray()),
                "Read-only mixed recovery changed physical bytes.");
            context.ObserveNovelty("checksum-migration", version, dirty, fixture.Password != null);
        }
        context.Metrics["migrationCases"] = context.Steps;
        context.Metrics["transactionBatches"] = context.Steps * 8;
        return Task.CompletedTask;
    }
}
