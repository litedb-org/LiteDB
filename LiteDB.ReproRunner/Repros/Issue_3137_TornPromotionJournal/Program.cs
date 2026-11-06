using LiteDB;
using LiteDB.Engine;
using LiteDB.ReproRunner.Shared;
using LiteDB.ReproRunner.Shared.Messaging;

namespace Issue_3137_TornPromotionJournal;

internal static class Program
{
    private static int Main()
    {
        var host = ReproHostClient.CreateDefault();
        ReproConfigurationReporter.SendConfiguration(host);
        typeof(LiteDatabase).Assembly.GetType("LiteDB.LiteDBPragmas")?
            .GetMethod("I_AM_AWARE_MY_DATABASE_BREAKS_WHEN_I_USE_THIS")?.Invoke(null, null);
        var directory = Path.Combine(ReproContext.FromEnvironment().SharedDatabaseRoot ?? Path.GetTempPath(),
            "torn-promotion-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var results = new[] { Run(directory, null), Run(directory, "secret") };
            Require(results.Distinct().Count() == 1, "Mixed plain/encrypted outcomes");
            var reproduced = results[0];
            var message = reproduced ? "REPRODUCED: durable journal refused after promotion erased primary identity."
                : "FIXED: durable journal recovers complete rows and indexes after identity erasure.";
            Console.WriteLine(message);
            host.SendResult(reproduced, message);
            return reproduced ? 0 : 1;
        }
        catch (Exception error)
        {
            host.SendResult(false, "INCONCLUSIVE: " + error);
            Console.Error.WriteLine(error);
            return 2;
        }
        finally { Directory.Delete(directory, true); }
    }

    private static bool Run(string directory, string? password)
    {
        using var data = new TornData(password == null ? 0 : 8192);
        using var wal = new MemoryStream();
        LiteDatabase Open(CompactStorageMode mode) => new(new LiteEngine(new EngineSettings
        {
            DataStream = data, LogStream = wal, Password = password, CompactStorage = mode, DurableCommits = true
        }));
        using (var db = Open(CompactStorageMode.Legacy))
        {
            db.CheckpointSize = 0;
            var rows = db.GetCollection("rows");
            rows.EnsureIndex("value");
            rows.Insert(Enumerable.Range(1, 3).Select(Row));
            db.Checkpoint();
            db.GetCollection("pending").Insert(new BsonDocument { ["_id"] = 42 });
            rows.Insert(Row(4));
        }
        using (var control = Open(CompactStorageMode.Legacy)) Verify(control);
        using (var db = Open(CompactStorageMode.Auto))
        {
            data.Armed = true;
            try { db.GetCollection("rows").Insert(Row(5)); throw new InvalidOperationException("No header tear"); }
            catch (IOException error) when (error.Message == "torn promotion header") { }
        }
        Require(data.Fired, "Did not reach promotion header write");
        var primary = data.ToArray();
        var journal = wal.ToArray();
        Require(journal.Length >= 16384, "Missing durable journal");
        var filename = Path.Combine(directory, password == null ? "plain.db" : "encrypted.db");
        var logname = Path.Combine(directory, password == null ? "plain-log.db" : "encrypted-log.db");
        File.WriteAllBytes(filename, primary);
        File.WriteAllBytes(logname, journal);
        var refused = 0;
        foreach (var readOnly in new[] { true, true, false, false })
        {
            try
            {
                using var db = new LiteDatabase(new ConnectionString
                {
                    Filename = filename, Password = password, ReadOnly = readOnly
                });
                Verify(db);
            }
            catch (LiteException error) when (error.ErrorCode == LiteException.INVALID_DATABASE &&
                error.Message.Contains("modern header journal") && error.Message.Contains("preserve both files"))
            {
                refused++;
            }
            if (readOnly || refused != 0)
            {
                Require(File.ReadAllBytes(filename).SequenceEqual(primary), "Primary changed on read-only/refused open");
                Require(File.ReadAllBytes(logname).SequenceEqual(journal), "Journal changed on read-only/refused open");
            }
        }
        Require(refused == 0 || refused == 4, "Inconsistent recovery");
        return refused == 4;
    }

    private static BsonDocument Row(int id) => new()
    {
        ["_id"] = id, ["value"] = "row-" + id,
        ["longRepeatedFieldName"] = id, ["anotherLongRepeatedFieldName"] = "payload",
        ["nestedDocument"] = new BsonDocument { ["longNestedFieldName"] = id, ["anotherNestedFieldName"] = true },
        ["arrayValues"] = new BsonArray(Enumerable.Range(1, 30).Select(x => new BsonValue(x)))
    };

    private static void Verify(LiteDatabase db)
    {
        var rows = db.GetCollection("rows");
        Require(rows.FindAll().OrderBy(x => x["_id"]).Select(x => x.ToString())
            .SequenceEqual(Enumerable.Range(1, 4).Select(x => Row(x).ToString())), "Committed rows differ");
        Require(rows.Find(Query.EQ("value", "row-4")).Single().ToString() == Row(4).ToString(), "Index differs");
        Require(db.GetCollection("pending").Exists("_id = 42"), "Committed collection lost");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed class TornData(long headerPosition) : MemoryStream
    {
        internal bool Armed, Fired;
        public override void Write(byte[] buffer, int offset, int count)
        {
            if (Armed && Position == headerPosition && count == 8192)
            {
                Armed = false;
                Fired = true;
                base.Write(buffer, offset, 59);
                base.Write(new byte[8192 - 59], 0, 8192 - 59);
                throw new IOException("torn promotion header");
            }
            base.Write(buffer, offset, count);
        }
    }
}
