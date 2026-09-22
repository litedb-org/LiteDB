using System;
using System.IO;
using System.Linq;
using LiteDB.Engine;

namespace LiteDB.Tests.Engine
{
    /// <summary>Shared by deterministic tests and the randomized compact power-loss target.</summary>
    internal static class PromotionPowerLossScenario
    {
        internal static readonly string[] Phases =
        {
            "promotion-before-journal-write", "promotion-after-journal-page-write",
            "promotion-after-journal-flush", "promotion-before-header-write",
            "promotion-after-header-write", "promotion-after-header-flush",
            "wal-page-before-write", "wal-page-after-write",
            "wal-confirmation-before-write", "wal-confirmation-after-write",
            "wal-before-durable-flush", "wal-after-durable-flush",
            "checkpoint-before-page-write", "checkpoint-after-page-write",
            "checkpoint-before-data-flush", "checkpoint-after-data-flush",
            "promotion-before-journal-retire-flush", "promotion-after-journal-retire-flush"
        };

        internal static void Run(string password, bool vector, string phase, int occurrence = 1,
            int tornPrefix = -1, string recoveryPhase = null, bool readOnly = false, bool damage = false,
            int tornJournalPart = -1, bool cachedJournal = false, bool tearRecovery = false, bool keepVectorJournal = false)
        {
            using var data = new PromotionPowerLossStream();
            using var log = new PromotionPowerLossStream();
            using (var baseline = Open(data, log, password, CompactStorageMode.Legacy))
            {
                baseline.CheckpointSize = 0;
                var rows = baseline.GetCollection("rows");
                rows.EnsureIndex("value", "longRepeatedFieldName");
                rows.Insert(Enumerable.Range(1, 3).Select(Document));
                if (keepVectorJournal) baseline.Checkpoint();
                if (vector) baseline.GetCollection("vectors").Insert(new BsonDocument
                {
                    ["_id"] = 1, ["vector"] = new BsonVector(new[] { 1f, 0f })
                });
                if (!keepVectorJournal) baseline.Checkpoint();
                // The journal must not lose a committed header newer than the data header.
                baseline.GetCollection("pendingCollection").Insert(new BsonDocument { ["_id"] = 42 });
                rows.Insert(Document(4));
            }

            var fired = false;
            var hits = 0;
            var acknowledged = false;
            LiteDatabase db = null;
            try
            {
                db = Open(data, log, password, CompactStorageMode.Auto);
                EngineState.SimulateProcessCrash = current =>
                {
                    if (current != phase || ++hits != occurrence) return;
                    if (tornPrefix >= 0)
                    {
                        var device = tornJournalPart < 0 ? data : log;
                        var writes = 0;
                        device.TearNextWrite = (bytes, offset, count) =>
                        {
                            if (tornJournalPart >= 0 && writes++ != tornJournalPart) return;
                            fired = true;
                            device.TearWrite(bytes, offset, count, tornPrefix, damage);
                            data.PowerCut();
                            log.PowerCut();
                        };
                    }
                    else
                    {
                        fired = true;
                        data.PowerCut();
                        log.PowerCut();
                        throw new IOException("Modeled power loss at " + phase);
                    }
                };
                db.BeginTrans();
                db.GetCollection("rows").Insert(Enumerable.Range(5, 4).Select(Document));
                db.Commit();
                acknowledged = true;
                db.Checkpoint();
            }
            catch (Exception) when (fired) { }
            finally
            {
                EngineState.SimulateProcessCrash = null;
                try { db?.Dispose(); } catch when (fired) { }
            }
            Require(fired, "Crash point was not reached: " + phase);
            var savedData = data.DurableBytes;
            var savedLog = log.DurableBytes;
            var cachedLog = cachedJournal ? log.ToArray() : null;

            if (recoveryPhase != null)
            {
                // Fail recovery twice: the only durable recovery image must survive both.
                for (var retry = 0; retry < 2; retry++)
                {
                    using var repairData = new PromotionPowerLossStream(savedData);
                    using var repairLog = new PromotionPowerLossStream(savedLog, cachedLog);
                    var interrupted = false;
                    EngineState.SimulateProcessCrash = current =>
                    {
                        if (current != recoveryPhase) return;
                        if (tearRecovery)
                        {
                            repairData.TearNextWrite = (bytes, offset, count) =>
                            {
                                interrupted = true;
                                repairData.TearWrite(bytes, offset, count, 59, true);
                                repairLog.PowerCut();
                            };
                            return;
                        }
                        interrupted = true;
                        repairData.PowerCut();
                        repairLog.PowerCut();
                        throw new IOException("Modeled recovery power loss");
                    };
                    try { using var ignored = Open(repairData, repairLog, password, CompactStorageMode.Auto); }
                    catch (Exception) when (interrupted) { }
                    finally { EngineState.SimulateProcessCrash = null; }
                    Require(interrupted, "Recovery crash point was not reached");
                    savedData = repairData.DurableBytes;
                    savedLog = repairLog.DurableBytes;
                    cachedLog = null;
                }
            }

            using var recoveredData = new PromotionPowerLossStream(savedData);
            using var recoveredLog = new PromotionPowerLossStream(savedLog);
            using (var recovered = Open(recoveredData, recoveredLog, password, CompactStorageMode.Auto, readOnly))
            {
                Verify(recovered, acknowledged, vector);
                if (!readOnly)
                {
                    recovered.Checkpoint();
                    // A new WAL epoch must never replay a stale promotion header.
                    recovered.GetCollection("afterRecovery").Insert(new BsonDocument { ["_id"] = 99 });
                }
            }
            if (readOnly)
            {
                Require(savedData.SequenceEqual(recoveredData.ToArray()), "Read-only recovery changed the data file");
                Require(savedLog.SequenceEqual(recoveredLog.ToArray()), "Read-only recovery changed the WAL");
            }
            else
            {
                using var finalData = new PromotionPowerLossStream(recoveredData.DurableBytes);
                using var finalLog = new PromotionPowerLossStream(recoveredLog.DurableBytes);
                using var final = Open(finalData, finalLog, password, CompactStorageMode.Auto);
                Verify(final, acknowledged, vector);
                Require(final.GetCollection("afterRecovery").Exists("_id = 99"), "New WAL epoch was lost");
                final.Checkpoint();
            }
        }

        private static void Verify(LiteDatabase db, bool acknowledged, bool vector)
        {
            var actual = db.GetCollection("rows").Query().OrderBy("_id").ToArray();
            Require(actual.Length == 8 || (!acknowledged && actual.Length == 4), "Partial/lost transaction after power loss");
            for (var i = 0; i < actual.Length; i++)
                Require(BsonSerializer.Serialize(actual[i]).SequenceEqual(BsonSerializer.Serialize(Document(i + 1))),
                    "Document differs after recovery");
            Require(db.GetCollection("pendingCollection").Exists("_id = 42"), "Committed collection header was lost");
            if (vector)
            {
                var value = db.GetCollection("vectors").FindById(1)?["vector"];
                Require(value != null && value.Equals(new BsonVector(new[] { 1f, 0f })), "Committed vector was lost");
            }
            var indexed = db.GetCollection("rows").Query().Where("longRepeatedFieldName >= 1")
                .OrderBy("longRepeatedFieldName").ToArray();
            Require(indexed.Select(x => x["_id"].AsInt32).SequenceEqual(Enumerable.Range(1, actual.Length)),
                "Secondary index differs after recovery");
        }

        private static BsonDocument Document(int id) => new BsonDocument
        {
            ["_id"] = id, ["longRepeatedFieldName"] = id,
            ["anotherLongRepeatedFieldName"] = "payload",
            ["nestedDocument"] = new BsonDocument { ["longNestedFieldName"] = id, ["anotherNestedFieldName"] = true },
            ["arrayValues"] = new BsonArray(Enumerable.Range(1, 30).Select(x => new BsonValue(x)))
        };

        private static LiteDatabase Open(PromotionPowerLossStream data, PromotionPowerLossStream log,
            string password, CompactStorageMode mode, bool readOnly = false) => new LiteDatabase(new LiteEngine(new EngineSettings
            {
                DataStream = data, LogStream = log, Password = password, CompactStorage = mode,
                ReadOnly = readOnly, DurableCommits = true, TransactionPageLimit = 4
            }));

        private static void Require(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }
    }
}
