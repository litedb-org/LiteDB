using System;
using System.IO;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Threading;
using LiteDB.Engine;
using LiteDB.Tests.Engine;

namespace LiteDB.Internals
{
    /// <summary>Small durable/volatile devices shared by regression tests and fuzzing.</summary>
    internal static class MvccRetirementScenario
    {
        internal static readonly string[] Phases =
        {
            "promotion-before-journal-write", "promotion-after-journal-flush", "promotion-before-header-write",
            "promotion-after-header-write", "promotion-after-header-flush", "promotion-after-journal-retire-flush",
            "retirement-before-record-write", "retirement-after-record-write", "retirement-records-flushed",
            "retirement-before-header-write", "retirement-after-header-write", "retirement-header-flushed",
            "retirement-before-journal-retire-flush", "retirement-journal-retired",
            "wal-slot-cleared", "wal-slots-flushed", "wal-slots-published", "reuse"
        };

        internal static BsonDocument Document(int id, int value) => new BsonDocument
        {
            ["_id"] = id, ["value"] = value, ["longRepeatedPayloadField"] = new string((char)('a' + value), 1500),
            ["longRepeatedArrayField"] = new BsonArray(id, value, "preserved"),
            ["nested"] = new BsonDocument { ["originalId"] = id, ["generation"] = value }
        };

        internal static void Run(string password, bool compact, string phase, int prefix = -1,
            bool repeatRepair = false, Action<byte[], byte[]> inspect = null, bool failFlush = false, int priorReclaims = 0, int historyRounds = 1, bool interleaveSafepoint = false)
        {
            using var data = new PromotionPowerLossStream();
            using var log = new PromotionPowerLossStream();
            LiteEngine engine = null;
            LiteDatabase db = null;
            IBsonDataReader snapshot = null;
            var fired = false;
            try
            {
                engine = new LiteEngine(Settings(data, log, password, compact));
                db = new LiteDatabase(engine, disposeOnClose: false);
                db.CheckpointSize = 0;
                db.GetCollection("rows").EnsureIndex("value");
                db.GetCollection("rows").Insert(Enumerable.Range(1, 8).Select(id => Document(id, 0)));
                db.GetCollection("untouched").Insert(Document(42, 0));
                db.Checkpoint();
                for (var round = 0; round < historyRounds; round++)
                    for (var value = 1; value <= 5; value++) Update(db, value);
                snapshot = engine.Query("rows", new Query());
                Worker(() => { for (var value = 6; value <= 9; value++) Update(db, value); });
                for (var round = 0; round < priorReclaims; round++)
                    Worker(() =>
                    {
                        engine.Checkpoint();
                        for (var value = 6; value <= 9; value++) Update(db, value);
                    });
                if (interleaveSafepoint) SafepointAcrossCheckpoint(engine, db);
                Action cut = () =>
                {
                    fired = true;
                    data.PowerCut();
                    log.PowerCut();
                    throw new IOException("Modeled MVCC power loss");
                };
                Action<string> crash = current =>
                {
                    if (current != phase || fired) return;
                    if (failFlush)
                    {
                        var flushTarget = phase.EndsWith("before-header-write", StringComparison.Ordinal) ? data : log;
                        flushTarget.BeforeFlush = cut;
                        return;
                    }
                    if (prefix < 0) cut();
                    var target = phase.EndsWith("before-header-write", StringComparison.Ordinal) ? data : log;
                    target.TearNextWrite = (bytes, offset, count) =>
                    {
                        fired = true;
                        target.TearWrite(bytes, offset, count, prefix, damage: true);
                        data.PowerCut();
                        log.PowerCut();
                    };
                };
                if (phase == "reuse")
                {
                    Worker(() => engine.Checkpoint());
                    var end = (log.Length - (password == null ? 0 : Constants.PAGE_SIZE)) / WalChecksum.FrameSize * Constants.PAGE_SIZE;
                    engine.SimulateDiskWriteFail = page =>
                    {
                        if (page.Position < end && !page.ReadBool(BasePage.P_IS_CONFIRMED)) crash("reuse");
                    };
                    Worker(() => Update(db, 10));
                }
                else
                {
                    engine.CheckpointStage = crash;
                    EngineState.SimulateProcessCrash = crash;
                    Worker(() => engine.Checkpoint());
                }
                if (phase == null)
                {
                    var seen = 0;
                    while (snapshot.Read())
                    {
                        Equal(snapshot.Current.AsDocument, Document(snapshot.Current["_id"].AsInt32, 5));
                        seen++;
                    }
                    Require(seen == 8, "Snapshot lost rows during retirement");
                    inspect?.Invoke(data.DurableBytes, log.DurableBytes);
                    Verify(data.DurableBytes, log.DurableBytes, password);
                    return;
                }
            }
            catch (Exception) when (fired) { }
            finally
            {
                EngineState.SimulateProcessCrash = null;
                if (engine != null) { engine.CheckpointStage = null; engine.SimulateDiskWriteFail = null; }
                try { snapshot?.Dispose(); } catch when (fired) { }
                try { db?.Dispose(); engine?.Dispose(); } catch when (fired) { }
            }
            Require(fired, "MVCC fault point not reached: " + phase);
            var savedData = data.DurableBytes;
            var savedLog = log.DurableBytes;
            inspect?.Invoke(savedData, savedLog);
            if (repeatRepair)
            {
                for (var retry = 0; retry < 2; retry++)
                {
                    using var repairData = new PromotionPowerLossStream(savedData);
                    using var repairLog = new PromotionPowerLossStream(savedLog);
                    var repairFired = false;
                    try
                    {
                        EngineState.SimulateProcessCrash = point =>
                        {
                            if (point != "promotion-recovery-before-header-write") return;
                            repairData.TearNextWrite = (bytes, offset, count) =>
                            {
                                repairFired = true;
                                repairData.TearWrite(bytes, offset, count, 59, damage: true);
                                repairLog.PowerCut();
                            };
                        };
                        using var repair = new LiteEngine(Settings(repairData, repairLog, password, compact));
                    }
                    catch (Exception) when (repairFired) { }
                    finally { EngineState.SimulateProcessCrash = null; }
                    Require(repairFired, "Repeated recovery did not exercise a header repair");
                    savedData = repairData.DurableBytes;
                    savedLog = repairLog.DurableBytes;
                }
            }
            Verify(savedData, savedLog, password);
        }

        private static EngineSettings Settings(Stream data, Stream log, string password, bool compact) => new EngineSettings
        {
            DataStream = data, LogStream = log, Password = password, TransactionPageLimit = 1,
            CompactStorage = compact ? CompactStorageMode.Auto : CompactStorageMode.Legacy
        };

        internal static void Verify(byte[] dataBytes, byte[] logBytes, string password)
        {
            using var data = new MemoryStream();
            using var log = new MemoryStream();
            data.Write(dataBytes, 0, dataBytes.Length);
            log.Write(logBytes, 0, logBytes.Length);
            foreach (var readOnly in new[] { true, false, true })
            {
                var settings = Settings(data, log, password, false);
                settings.ReadOnly = readOnly;
                using (var engine = new LiteEngine(settings))
                using (var db = new LiteDatabase(engine, disposeOnClose: false))
                {
                    VerifyDatabase(db);
                    if (!readOnly) db.Checkpoint();
                }
                if (readOnly)
                    Require(data.ToArray().SequenceEqual(dataBytes) && log.ToArray().SequenceEqual(logBytes),
                        "Read-only MVCC recovery changed source bytes");
                else { dataBytes = data.ToArray(); logBytes = log.ToArray(); }
            }
        }

        internal static void VerifyDatabase(LiteDatabase db)
        {
            var rows = db.GetCollection("rows");
            var actual = rows.FindAll().OrderBy(row => row["_id"].AsInt32).ToArray();
            Require(actual.Length == 8, "Recovery lost or duplicated documents");
            for (var i = 0; i < actual.Length; i++) Equal(actual[i], Document(i + 1, 9));
            var indexed = rows.Find(Query.EQ("value", 9)).OrderBy(row => row["_id"].AsInt32).ToArray();
            Require(indexed.Length == 8, "Recovery lost secondary-index results");
            for (var i = 0; i < indexed.Length; i++) Equal(indexed[i], Document(i + 1, 9));
            Require(!rows.Find(Query.EQ("value", 5)).Any(), "Retired keys remain visible");
            Equal(db.GetCollection("untouched").FindById(42), Document(42, 0));
        }

        private static void Update(LiteDatabase db, int value) =>
            db.GetCollection("rows").Update(Enumerable.Range(1, 8).Select(id => Document(id, value)));

        private static void Equal(BsonDocument left, BsonDocument right) =>
            Require(BsonSerializer.Serialize(left).SequenceEqual(BsonSerializer.Serialize(right)), "Full document payload differs");

        private static void Require(bool value, string message)
        {
            if (!value) throw new InvalidOperationException(message);
        }

        private static void SafepointAcrossCheckpoint(LiteEngine engine, LiteDatabase db)
        {
            using var ready = new ManualResetEventSlim();
            using var continueWriter = new ManualResetEventSlim();
            Exception failure = null;
            var writer = new Thread(() =>
            {
                try
                {
                    db.BeginTrans();
                    Update(db, 10);
                    engine.GetMonitor().GetThreadTransaction().Safepoint();
                    ready.Set();
                    Require(continueWriter.Wait(TimeSpan.FromSeconds(20)), "Checkpointer did not release writer");
                    Update(db, 9);
                    engine.GetMonitor().GetThreadTransaction().Safepoint();
                    db.Commit();
                }
                catch (Exception error) { failure = error; ready.Set(); }
            }) { IsBackground = true };
            writer.Start();
            try
            {
                Require(ready.Wait(TimeSpan.FromSeconds(20)), "Writer did not reach safepoint");
                if (failure != null) ExceptionDispatchInfo.Capture(failure).Throw();
                engine.Checkpoint();
            }
            finally
            {
                continueWriter.Set();
                Require(writer.Join(TimeSpan.FromSeconds(20)), "Writer could not finish after retirement");
            }
            if (failure != null) ExceptionDispatchInfo.Capture(failure).Throw();
        }

        private static void Worker(Action action)
        {
            Exception failure = null;
            var worker = new Thread(() => { try { action(); } catch (Exception error) { failure = error; } }) { IsBackground = true };
            worker.Start();
            Require(worker.Join(TimeSpan.FromSeconds(20)), "MVCC worker was blocked by a reader");
            if (failure != null) ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }
}
