using System;
using System.IO;
using System.Threading;
using FluentAssertions;
using LiteDB.Engine;
using Xunit;

namespace LiteDB.Tests.Regressions
{
    /// <summary>
    /// Regression since 5.0.21: a transaction's unconfirmed WAL page is rewritten in place at its
    /// previous slot on the next safepoint (e9bec08f8), and recovery stops at the first frame whose
    /// checksum fails (bef9aa3c3), truncating everything after it (DiscardWalTail). When that
    /// in-place rewrite failed part-way, later acknowledged commits could be appended behind the
    /// torn frame and were discarded by the next crash recovery:
    /// - with an exception the engine did not treat as fatal (any non-IOException, e.g.
    ///   UnauthorizedAccessException for EACCES/EPERM or an exception from a caller's log stream),
    ///   the transaction was rolled back and the engine kept committing;
    /// - with an IOException, a committer already waiting for the WAL writer got in before the
    ///   engine stopped.
    /// A failed overwrite now stops the engine before the WAL writer is released.
    /// 5.0.21 only appended WAL frames and had no frame CRC, so later commits were recovered.
    /// </summary>
    [Trait("Category", "RegressionSince5021")]
    public class TornSlotRewrite_Tests
    {
        [Fact]
        public void Failed_in_place_rewrite_stops_the_engine_and_keeps_every_acknowledged_commit()
        {
            using var data = new MemoryStream();
            using var log = new FailingLogStream(() => throw new UnauthorizedAccessException("injected partial overwrite (EACCES)"));
            var settings = new EngineSettings { DataStream = data, LogStream = log, TransactionPageLimit = 2 };

            byte[] crashData, crashLog;
            using (var engine = new LiteEngine(settings))
            using (var db = new LiteDatabase(engine, disposeOnClose: false))
            {
                PrepareSafepointedTransaction(engine, db);
                var length = log.Length;

                log.Armed = true;
                Action update = () => db.GetCollection("a").Update(new BsonDocument { ["_id"] = 1, ["value"] = 3 });
                update.Should().Throw<IOException>().WithInnerException<UnauthorizedAccessException>();
                (length - log.FailedPosition).Should().BeGreaterOrEqualTo(WalChecksum.FrameSize,
                    "the failure must tear an existing frame in place, not an append into the tail padding");

                // Nothing may be appended behind the torn frame: the stopped engine rethrows its failure.
                Action insert = () => db.GetCollection("b").Insert(new BsonDocument { ["_id"] = 2 });
                insert.Should().Throw<IOException>().WithMessage("*WAL frame write failed*")
                    .Where(ex => ex.GetBaseException() is UnauthorizedAccessException);

                // Process crash: take the bytes before any close-time work.
                crashData = data.ToArray();
                crashLog = log.ToArray();
            }

            var (value, second) = Recover(crashData, crashLog);
            value.Should().Be(0, "the aborted transaction must not be recovered");
            second.Should().BeFalse();
        }

#if DEBUG || TESTING
        /// <summary>
        /// The IOException case: a commit that reaches the WAL writer right after the failed rewrite
        /// released it, before the engine's teardown, finds the engine stopped. A test hook runs the
        /// commit on another thread in exactly that window.
        /// </summary>
        [Fact]
        public void Commit_waiting_for_the_writer_during_a_failed_rewrite_is_never_acknowledged_and_lost()
        {
            using var data = new MemoryStream();
            using var log = new FailingLogStream(() => throw new IOException("injected partial overwrite"));
            var settings = new EngineSettings { DataStream = data, LogStream = log, TransactionPageLimit = 2 };

            byte[] crashData, crashLog;
            var acknowledged = false;
            var windowRan = false;
            using (var engine = new LiteEngine(settings))
            using (var db = new LiteDatabase(engine, disposeOnClose: false))
            {
                PrepareSafepointedTransaction(engine, db);
                engine.SimulateAfterFailedWalWrite = () =>
                {
                    engine.SimulateAfterFailedWalWrite = null;
                    windowRan = true;
                    var committer = new Thread(() =>
                    {
                        try
                        {
                            db.GetCollection("b").Insert(new BsonDocument { ["_id"] = 2 });
                            acknowledged = true;
                        }
                        catch (Exception) { }
                    });
                    committer.Start();
                    committer.Join(TimeSpan.FromSeconds(30)).Should().BeTrue();
                };

                log.Armed = true;
                Action update = () => db.GetCollection("a").Update(new BsonDocument { ["_id"] = 1, ["value"] = 3 });
                update.Should().Throw<IOException>();
                windowRan.Should().BeTrue("the commit ran after the failed write released the WAL writer");

                crashData = data.ToArray();
                crashLog = log.ToArray();
            }

            var (value, second) = Recover(crashData, crashLog);
            value.Should().Be(0);
            if (acknowledged) second.Should().BeTrue("an acknowledged commit must survive crash recovery");
            acknowledged.Should().BeFalse("the engine stopped before it released the WAL writer");
        }
#endif

        private static void PrepareSafepointedTransaction(LiteEngine engine, LiteDatabase db)
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
        }

        private static (int value, bool second) Recover(byte[] crashData, byte[] crashLog)
        {
            using var data = new MemoryStream();
            data.Write(crashData, 0, crashData.Length);
            using var log = new MemoryStream();
            log.Write(crashLog, 0, crashLog.Length);
            using var engine = new LiteEngine(new EngineSettings { DataStream = data, LogStream = log });
            using var db = new LiteDatabase(engine, disposeOnClose: false);
            ((object)db.GetCollection("b").FindById(1)).Should().NotBeNull("commits acknowledged before the failure must survive");
            return (db.GetCollection("a").FindById(1)["value"].AsInt32, db.GetCollection("b").FindById(2) != null);
        }

        private sealed class FailingLogStream : MemoryStream
        {
            private readonly Action _fail;
            public FailingLogStream(Action fail) { _fail = fail; }
            public bool Armed { get; set; }
            public long FailedPosition { get; private set; }

            public override void Write(byte[] buffer, int offset, int count)
            {
                // Tear only an overwrite of existing bytes (the in-place slot rewrite).
                if (this.Armed && this.Position < this.Length)
                {
                    this.Armed = false;
                    this.FailedPosition = this.Position;
                    base.Write(buffer, offset, Math.Min(count, 127));
                    _fail();
                }
                base.Write(buffer, offset, count);
            }
        }
    }
}
