using System;
using System.IO;
using System.Linq;
using FluentAssertions;
using LiteDB.Engine;
using Xunit;

namespace LiteDB.Tests.Regressions
{
    /// <summary>
    /// A WAL append that fails part-way can leave a torn frame at the end of the WAL; the failed
    /// write then truncates it. If that truncation fails too, the torn frame stays, and recovery
    /// stops at it: a commit written behind it would be lost. Two things keep that from happening.
    /// An I/O failure stops the engine (a reopen's recovery discards the torn tail). Any other
    /// failure of a safepoint write only rolls back its transaction, but the failed append's
    /// position is released before the truncation, so the next append (every commit appends at
    /// least its confirmation) rewrites that slot with a complete frame, never behind it.
    /// Checked with a frame torn at half its length and with a complete frame whose write still
    /// reported failure, for the first and second frame written after the failure is armed.
    /// </summary>
    [Trait("Category", "RegressionSince5021")]
    public class TornWalAppend_Tests
    {
        [Theory]
        [InlineData(1, false, false)]
        [InlineData(2, false, false)]
        [InlineData(1, true, false)]
        [InlineData(2, true, false)]
        [InlineData(1, false, true)]
        [InlineData(2, false, true)]
        [InlineData(1, true, true)]
        [InlineData(2, true, true)]
        public void Later_commits_survive_a_torn_append_whose_truncation_fails(int frame, bool completeFrame, bool ioFailure)
        {
            using var data = new MemoryStream();
            using var log = new TornLog { IoFailure = ioFailure };
            // Every page beyond the first is written by a safepoint, before the commit.
            var settings = new EngineSettings { DataStream = data, LogStream = log, TransactionPageLimit = 1 };
            using (var engine = new LiteEngine(settings))
            using (var db = new LiteDatabase(engine, disposeOnClose: false))
            {
                db.CheckpointSize = 0;
                db.GetCollection("rows").Insert(Enumerable.Range(1, 20).Select(id => Row(id, 0)));

                log.TearFrame = frame;
                log.CompleteFrame = completeFrame;
                log.FailSetLength = true;
                Action failed = () => db.GetCollection("rows").Insert(Enumerable.Range(100, 30).Select(id => Row(id, 0)));
                if (ioFailure) failed.Should().Throw<IOException>();
                else failed.Should().Throw<InvalidOperationException>();
                log.Torn.Should().BeTrue("the write tore a frame");
                log.SetLengthFailed.Should().BeTrue("the truncation of the torn frame failed too");
                log.FailSetLength = false;

                // An I/O failure stopped the engine; any other failure only rolled back.
                if (!ioFailure) WriteLater(db);
            }
            if (ioFailure)
            {
                using var reopened = new LiteDatabase(new LiteEngine(new EngineSettings { DataStream = data, LogStream = log }));
                reopened.CheckpointSize = 0;
                WriteLater(reopened);
            }

            // A killed process leaves every byte the streams hold.
            using var recoveredData = new MemoryStream(data.ToArray());
            using var recoveredLog = new MemoryStream(log.ToArray());
            using var recovered = new LiteDatabase(new LiteEngine(new EngineSettings { DataStream = recoveredData, LogStream = recoveredLog }));
            var docs = recovered.GetCollection("rows").FindAll().ToList();
            docs.Select(x => x["_id"].AsInt32).Should().BeEquivalentTo(Enumerable.Range(1, 20).Concat(new[] { 200 }),
                "every acknowledged commit survives, and the failed insert is absent");
            docs.Where(x => x["_id"].AsInt32 <= 20).Should().OnlyContain(x => x["value"].AsInt32 == 7);
        }

        private static void WriteLater(LiteDatabase db)
        {
            var rows = db.GetCollection("rows");
            rows.Update(Enumerable.Range(1, 20).Select(id => Row(id, 7))).Should().Be(20);
            rows.Insert(Row(200, 0));
            rows.Count().Should().Be(21);
        }

        private static BsonDocument Row(int id, int value) => new BsonDocument
        {
            ["_id"] = id, ["value"] = value, ["payload"] = new string('p', 500)
        };

        /// <summary>
        /// A log stream whose <see cref="TearFrame"/>-th frame write after arming stores half the
        /// frame (or all of it) and then fails, and whose SetLength then fails while armed. The
        /// failure is an IOException or, for a caller stream, any other exception.
        /// </summary>
        private sealed class TornLog : MemoryStream
        {
            internal int TearFrame;
            internal bool IoFailure, CompleteFrame, FailSetLength, Torn, SetLengthFailed;

            public override void Write(byte[] buffer, int offset, int count)
            {
                if (TearFrame > 0 && count == WalChecksum.FrameSize && --TearFrame == 0)
                {
                    base.Write(buffer, offset, CompleteFrame ? count : count / 2);
                    Torn = true;
                    throw Failure("injected torn frame write");
                }
                base.Write(buffer, offset, count);
            }

            public override void SetLength(long value)
            {
                if (FailSetLength && Torn)
                {
                    SetLengthFailed = true;
                    throw Failure("injected truncation failure");
                }
                base.SetLength(value);
            }

            private Exception Failure(string message) =>
                IoFailure ? new IOException(message) : new InvalidOperationException(message);
        }
    }
}
