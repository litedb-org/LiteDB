using System.Reflection;
using LiteDB;
using LiteDB.Engine;
using LiteDB.ReproRunner.Shared;
using LiteDB.ReproRunner.Shared.Messaging;

namespace Issue_3027_FailedSafepointWrite;

/// <summary>
/// Defect fixed by PR #3027 (guard: FailedSafepointWrite_Tests). A transaction that holds more
/// pages than its limit (TransactionPageLimit) writes its dirty pages to the WAL at a safepoint before
/// its commit, and any operation can reach one, a query too. When that write failed with an exception
/// the engine does not treat as fatal (any non-I/O exception: UnauthorizedAccessException for
/// EACCES/EPERM, or an exception from a caller's log stream), the known-bad engine only reported it to
/// the query: the explicit transaction stayed active although the failed pages had been discarded, and
/// Commit then published the rest, a database whose every read fails ("get only index below highest
/// index"), also after reopening. Fixed: such a transaction can only roll back; a later read, write or
/// Commit throws "can only be rolled back" (a write or Commit rolls it back), and the data is intact.
///
/// Black box: the database runs on caller streams (EngineSettings.DataStream/LogStream) with
/// TransactionPageLimit = 6. An explicit transaction inserts three rows, then a query over a large
/// collection pushes it past its page limit; its safepoint writes the dirty pages, and the log stream
/// fails that frame write before writing anything.
///
/// Exit code 0: the defect reproduced (the known-bad LiteDB must do this). Exit code 1: the fixed
/// behavior was verified in full (the candidate must do this). Exit code 2: anything else.
/// </summary>
internal static class Program
{
    private const int Reproduced = 0;
    private const int Fixed = 1;
    private const int Inconclusive = 2;
    // A WAL frame: a page (8192 bytes) and its 64-byte checksum trailer.
    private const int FrameSize = 8192 + 64;
    private const string OnlyRollback = "can only be rolled back";
    private const string Injected = "injected WAL write failure";

    private static int Main()
    {
        var host = ReproHostClient.CreateDefault();
        ReproConfigurationReporter.SendConfiguration(host);
        var engine = typeof(LiteDatabase).Assembly;
        host.SendLog($"LiteDB {engine.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion} loaded from {engine.Location}");
        // A development prerelease build (LITEDB_PREDEV) refuses to open files until this is acknowledged.
        engine.GetType("LiteDB.LiteDBPragmas")?.GetMethod("I_AM_AWARE_MY_DATABASE_BREAKS_WHEN_I_USE_THIS")?.Invoke(null, null);

        try
        {
            var (code, summary) = Run(host);
            host.SendResult(code == Reproduced, summary);
            return code;
        }
        catch (Exception error)
        {
            host.SendResult(false, $"INCONCLUSIVE: {error.GetType().Name}: {error.Message}", new { Exception = error.ToString() });
            Console.Error.WriteLine(error);
            return Inconclusive;
        }
    }

    private static (int Code, string Summary) Run(ReproHostClient host)
    {
        // A read, then Commit, after the failed safepoint write.
        using (var data = new MemoryStream())
        using (var log = new FailingLog())
        {
            using (var engine = new LiteEngine(new EngineSettings { DataStream = data, LogStream = log, TransactionPageLimit = 6 }))
            using (var db = new LiteDatabase(engine, disposeOnClose: false))
            {
                FailSafepoint(host, db, log);
                var rows = db.GetCollection("rows");

                var readRefused = false;
                try
                {
                    host.SendLog($"FindById(1) after the failed safepoint write returned {Show(rows.FindById(1))}");
                }
                catch (LiteException error) when (error.Message.Contains(OnlyRollback))
                {
                    readRefused = true;
                    host.SendLog($"FindById(1) after the failed safepoint write threw: {error.Message}");
                }

                var commitRefused = false;
                try
                {
                    var committed = db.Commit();
                    host.SendLog($"Commit after the failed safepoint write returned {committed}");
                    if (committed)
                    {
                        var damage = Damage(db);
                        host.SendLog($"After that Commit: {damage ?? "every row reads back"}");
                        var reopened = Reopen(data, log, Damage);
                        host.SendLog($"After reopening the streams: {reopened ?? "every row reads back"}");
                        Require(damage != null && reopened != null, "Commit published the transaction whose safepoint write failed, but the database reads back intact");
                        return (Reproduced, "REPRODUCED: Commit published the transaction whose safepoint write failed; the database it left cannot be read, " +
                            $"also after reopening: {reopened}");
                    }
                }
                catch (LiteException error) when (error.Message.Contains(OnlyRollback))
                {
                    commitRefused = true;
                    host.SendLog($"Commit after the failed safepoint write threw: {error.Message}");
                }

                Require(readRefused, "a read in the transaction whose safepoint write failed was not refused");
                Require(commitRefused, "Commit of the transaction whose safepoint write failed was not refused");
                Require(!db.Rollback(), "the refused Commit did not end the transaction");
                AssertRows(db, "after the refused Commit");
                db.GetCollection("rows").Insert(Row(10));
            }
            Reopen(data, log, db => { AssertRows(db, "reopened", 10); return true; });
        }

        // A write, then Commit, after the failed safepoint write.
        using (var data = new MemoryStream())
        using (var log = new FailingLog())
        {
            using (var engine = new LiteEngine(new EngineSettings { DataStream = data, LogStream = log, TransactionPageLimit = 6 }))
            using (var db = new LiteDatabase(engine, disposeOnClose: false))
            {
                FailSafepoint(host, db, log);
                try
                {
                    db.GetCollection("rows").Insert(Row(5));
                    throw new InvalidOperationException("a write in the transaction whose safepoint write failed was not refused");
                }
                catch (LiteException error) when (error.Message.Contains(OnlyRollback))
                {
                    host.SendLog($"An insert after the failed safepoint write threw: {error.Message}");
                }
                Require(!db.Commit(), "the refused write did not roll the transaction back");
                AssertRows(db, "after the refused write");
                db.GetCollection("rows").Insert(Row(10));
            }
            Reopen(data, log, db => { AssertRows(db, "reopened after the refused write", 10); return true; });
        }

        return (Fixed, "FIXED: the transaction whose safepoint write failed can only be rolled back: a read, a write and Commit in it throw, " +
            "the committed data stays intact and the database takes and keeps new commits");
    }

    /// <summary>Row 1 committed and checkpointed; then a transaction inserts rows 2 to 4 and a query's safepoint fails to write them.</summary>
    private static void FailSafepoint(ReproHostClient host, LiteDatabase db, FailingLog log)
    {
        db.CheckpointSize = 0;
        db.GetCollection("big").Insert(Enumerable.Range(1, 100).Select(id => new BsonDocument { ["_id"] = id, ["payload"] = new string('b', 2000) }));
        db.GetCollection("rows").EnsureIndex("value");
        db.GetCollection("rows").Insert(Row(1));
        db.Checkpoint();

        Require(db.BeginTrans(), "no explicit transaction started");
        db.GetCollection("rows").Insert(Enumerable.Range(2, 3).Select(Row));
        log.FailNextFrame = true;
        try
        {
            var count = db.GetCollection("big").FindAll().Count();
            throw new InvalidOperationException($"the query read {count} documents without a failed safepoint write");
        }
        catch (Exception error) when (error.ToString().Contains(Injected))
        {
            host.SendLog($"The query whose safepoint wrote the transaction's pages threw {error.GetType().Name}: {error.Message}");
        }
        Require(log.Failed, "the log stream failed no frame write");
    }

    /// <summary>Null when the committed transaction reads back (rows 1 to 4, their index, the large collection), else what failed.</summary>
    private static string? Damage(LiteDatabase db)
    {
        try
        {
            var rows = db.GetCollection("rows");
            var all = rows.FindAll().Select(x => x["_id"].AsInt32).OrderBy(x => x).ToList();
            var indexed = rows.Find(Query.GTE("value", 0)).Select(x => x["_id"].AsInt32).OrderBy(x => x).ToList();
            var big = db.GetCollection("big").Count();
            var expected = Enumerable.Range(1, 4);
            return all.SequenceEqual(expected) && indexed.SequenceEqual(expected) && big == 100 ? null
                : $"rows [{string.Join(", ", all)}], by index [{string.Join(", ", indexed)}], {big} large documents";
        }
        catch (LiteException error)
        {
            return $"{error.GetType().Name}: {error.Message}";
        }
    }

    /// <summary>The transaction is absent as a whole, row 1 and the <paramref name="more"/> rows are there, and the large collection is intact.</summary>
    private static void AssertRows(LiteDatabase db, string when, params int[] more)
    {
        var rows = db.GetCollection("rows");
        var expected = new[] { 1 }.Concat(more).ToList();
        var all = rows.FindAll().Select(x => x["_id"].AsInt32).OrderBy(x => x).ToList();
        var indexed = rows.Find(Query.GTE("value", 0)).Select(x => x["_id"].AsInt32).OrderBy(x => x).ToList();
        Require(all.SequenceEqual(expected) && indexed.SequenceEqual(expected),
            $"{when}: rows [{string.Join(", ", all)}], by index [{string.Join(", ", indexed)}], expected [{string.Join(", ", expected)}]");
        for (var id = 2; id <= 5; id++) Require(rows.FindById(id) == null, $"{when}: row {id} of the rolled-back transaction is there");
        var first = rows.FindById(1);
        Require(first != null && first["payload"].AsString == new string('r', 500), $"{when}: FindById(1) returned {Show(first)}");
        var big = db.GetCollection("big").Count();
        Require(big == 100, $"{when}: {big} large documents, expected 100");
    }

    /// <summary>Opens a copy of the streams' bytes (what a killed process leaves) and runs <paramref name="check"/> on it.</summary>
    private static T Reopen<T>(MemoryStream data, MemoryStream log, Func<LiteDatabase, T> check)
    {
        using var db = new LiteDatabase(new LiteEngine(new EngineSettings
        {
            DataStream = new MemoryStream(data.ToArray()), LogStream = new MemoryStream(log.ToArray())
        }));
        return check(db);
    }

    private static string Show(BsonDocument? doc)
    {
        if (doc == null) return "null";
        var text = doc.ToString();
        return text.Length > 60 ? text.Substring(0, 60) + "..." : text;
    }

    private static BsonDocument Row(int id) => new BsonDocument
    {
        ["_id"] = id, ["value"] = id, ["payload"] = new string('r', 500)
    };

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    /// <summary>A caller log stream whose next page frame write fails before writing anything.</summary>
    private sealed class FailingLog : MemoryStream
    {
        // The frame marker of the candidate's header frame (a copy of the data header that starts a WAL), not a page.
        private const uint HeaderFrameMagic = 0x31524448;
        internal bool FailNextFrame, Failed;

        public override void Write(byte[] buffer, int offset, int count)
        {
            if (FailNextFrame && count == FrameSize && BitConverter.ToUInt32(buffer, offset + 8192 + 52) != HeaderFrameMagic)
            {
                FailNextFrame = false;
                Failed = true;
                throw new InvalidOperationException(Injected);
            }
            base.Write(buffer, offset, count);
        }
    }
}
