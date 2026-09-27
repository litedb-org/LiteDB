using System;
using System.IO;
using FluentAssertions;
using LiteDB.Engine;
using Xunit;

namespace LiteDB.Tests.Regressions
{
    /// <summary>
    /// Regression since 5.0.21: a transaction's unconfirmed WAL page is rewritten in place at its
    /// previous slot on the next safepoint (e9bec08f8), and recovery stops at the first frame whose
    /// checksum fails (bef9aa3c3), truncating everything after it (DiscardWalTail). When that
    /// in-place rewrite fails part-way with an exception the engine does not treat as fatal (any
    /// non-IOException, e.g. UnauthorizedAccessException for EACCES/EPERM or an exception from a
    /// caller's log stream), the explicit transaction is rolled back, the engine keeps running and
    /// the torn frame stays in the middle of the WAL. Every later acknowledged commit is appended
    /// after it and is discarded by the next crash recovery.
    /// 5.0.21 only appended WAL frames and had no frame CRC: the torn frame was an unconfirmed page
    /// of the aborted transaction, and later commits were recovered.
    /// </summary>
    [Trait("Category", "RegressionSince5021")]
    public class TornSlotRewrite_Tests
    {
        [Fact]
        public void Commit_after_a_failed_in_place_safepoint_rewrite_survives_crash_recovery()
        {
            using var data = new MemoryStream();
            using var log = new FailingLogStream();
            var settings = new EngineSettings { DataStream = data, LogStream = log, TransactionPageLimit = 2 };

            byte[] crashData, crashLog;
            using (var engine = new LiteEngine(settings))
            using (var db = new LiteDatabase(engine, disposeOnClose: false))
            {
                var a = db.GetCollection("a");
                var b = db.GetCollection("b");
                a.Insert(new BsonDocument { ["_id"] = 1, ["value"] = 0 });
                b.Insert(new BsonDocument { ["_id"] = 1 });
                db.Checkpoint();

                db.BeginTrans().Should().BeTrue();
                a.Update(new BsonDocument { ["_id"] = 1, ["value"] = 1 });
                engine.GetMonitor().GetThreadTransaction().Safepoint();
                a.Update(new BsonDocument { ["_id"] = 1, ["value"] = 2 });
                var length = log.Length;

                log.FailNextWrite = true;
                Action update = () => a.Update(new BsonDocument { ["_id"] = 1, ["value"] = 3 });
                update.Should().Throw<UnauthorizedAccessException>();
                (length - log.FailedPosition).Should().BeGreaterOrEqualTo(WalChecksum.FrameSize,
                    "the failure must tear an existing frame in place, not an append into the tail padding");

                // The engine is still usable: this commit is acknowledged (and flushed).
                b.Insert(new BsonDocument { ["_id"] = 2 });
                ((object)b.FindById(2)).Should().NotBeNull();

                // Process crash: take the bytes before any close-time checkpoint.
                crashData = data.ToArray();
                crashLog = log.ToArray();
            }

            using var recoveredData = new MemoryStream();
            recoveredData.Write(crashData, 0, crashData.Length);
            using var recoveredLog = new MemoryStream();
            recoveredLog.Write(crashLog, 0, crashLog.Length);
            using var reopenedEngine = new LiteEngine(new EngineSettings { DataStream = recoveredData, LogStream = recoveredLog });
            using var reopened = new LiteDatabase(reopenedEngine, disposeOnClose: false);

            reopened.GetCollection("a").FindById(1)["value"].AsInt32.Should().Be(0, "the aborted transaction must not be recovered");
            ((object)reopened.GetCollection("b").FindById(2)).Should().NotBeNull("an acknowledged commit must survive crash recovery");
        }

        private sealed class FailingLogStream : MemoryStream
        {
            public bool FailNextWrite { get; set; }
            public long FailedPosition { get; private set; }

            public override void Write(byte[] buffer, int offset, int count)
            {
                if (this.FailNextWrite)
                {
                    this.FailNextWrite = false;
                    this.FailedPosition = this.Position;
                    base.Write(buffer, offset, Math.Min(count, 127));
                    throw new UnauthorizedAccessException("injected partial overwrite (EACCES)");
                }
                base.Write(buffer, offset, count);
            }
        }
    }
}
