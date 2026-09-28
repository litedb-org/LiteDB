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
    /// stops at it: a commit written behind it would be lost. Such a write now stops the engine
    /// before the WAL writer is released, whatever the exception type, and records the failure: the
    /// engine's next call reopens it read-only (decision 6 of docs/decisions/durability-policy.md),
    /// so it reads what the files hold and refuses every write before it changes anything, and no
    /// commit is written after the torn frame; a later open's recovery discards the torn tail.
    /// When the truncation succeeds, a non-I/O failure of a safepoint write only rolls back, and
    /// the failed append's position is released, so the next append rewrites that slot instead
    /// of leaving a hole behind it.
    /// Checked with a frame torn at half its length and with a complete frame whose write still
    /// reported failure, for the first and second frame written after the failure is armed.
    /// </summary>
    [Trait("Category", "IoSafety")]
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
        public void Failed_truncation_of_a_torn_append_leaves_the_engine_read_only(int frame, bool completeFrame, bool ioFailure)
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

                log.Arm(frame, completeFrame, failSetLength: true);
                Action failed = () => db.GetCollection("rows").Insert(Enumerable.Range(100, 30).Select(id => Row(id, 0)));
                failed.Should().Throw<IOException>().Where(x => x.ToString().Contains("injected torn frame write"),
                    "the failure reported is the write that tore the frame, not its failed cleanup");
                log.Torn.Should().BeTrue("the write tore a frame");
                log.SetLengthFailed.Should().BeTrue("the truncation of the torn frame failed too");
                log.Disarm();

                // No commit may follow the torn frame: the engine continues read-only, reads the rows
                // acknowledged before the failure, and refuses every write before it changes a byte.
                var files = (Data: data.ToArray(), Log: log.ToArray());
                var rows = db.GetCollection("rows");
                rows.FindAll().Should().BeEquivalentTo(Enumerable.Range(1, 20).Select(id => Row(id, 0)), o => o.WithStrictOrdering());
                var record = ReadOnlyAfterWriteFailure.AssertReported(db, "A WAL write", "log",
                    ioFailure ? "injected torn frame write" : "WAL frame write failed.");
                ReadOnlyAfterWriteFailure.AssertWriteRefused(() => rows.Insert(Row(300, 0)), record);
                ReadOnlyAfterWriteFailure.AssertWriteRefused(() => rows.Update(Row(1, 7)), record);
                rows.Count().Should().Be(20);
                data.ToArray().Should().Equal(files.Data, "the read-only engine writes nothing");
                log.ToArray().Should().Equal(files.Log, "nothing is appended behind the torn frame");
            }

            using (var reopened = new LiteDatabase(new LiteEngine(new EngineSettings { DataStream = data, LogStream = log })))
            {
                reopened.CheckpointSize = 0;
                WriteLater(reopened);
            }
            AssertRecovered(data, log);
        }

        [Theory]
        [InlineData(1, false)]
        [InlineData(2, false)]
        [InlineData(1, true)]
        [InlineData(2, true)]
        public void Truncated_torn_append_is_rewritten_by_the_next_commit(int frame, bool completeFrame)
        {
            using var data = new MemoryStream();
            using var log = new TornLog();
            var settings = new EngineSettings { DataStream = data, LogStream = log, TransactionPageLimit = 1 };
            using (var engine = new LiteEngine(settings))
            using (var db = new LiteDatabase(engine, disposeOnClose: false))
            {
                db.CheckpointSize = 0;
                db.GetCollection("rows").Insert(Enumerable.Range(1, 20).Select(id => Row(id, 0)));

                log.Arm(frame, completeFrame, failSetLength: false);
                Action failed = () => db.GetCollection("rows").Insert(Enumerable.Range(100, 30).Select(id => Row(id, 0)));
                failed.Should().Throw<InvalidOperationException>("a non-I/O failure only rolls back");
                log.Torn.Should().BeTrue();
                log.Disarm();

                WriteLater(db);
            }
            AssertRecovered(data, log);
        }

        /// <summary>
        /// A caller log stream that buffers (a BufferedStream, a FileStream with a large buffer) held
        /// whole frames after their write returned and wrote them on later: at the next frame's
        /// write, at a seek or length query (also a reader's, see SharedBufferedLogStream_Tests) or at
        /// the batch's final flush, and could tear one then. The failed write truncated only its own
        /// frame and a non-I/O failure only rolled back: a later commit was acknowledged behind the
        /// torn frame and lost at recovery (acknowledged 1..20, 200, 201; recovered 1..20). Such a
        /// stream is now flushed after each write, so a failure tears the frame being written and
        /// fails its write, which truncates it. Checked with a BufferedStream of 64 KiB and with a
        /// stream that holds its latest write until the next write or flush and tears it at a flush
        /// ("flush") or at the next frame's write ("frame": never reached now, nothing is held then).
        /// </summary>
        [Theory]
        [InlineData("buffered", false)]
        [InlineData("buffered", true)]
        [InlineData("frame", false)]
        [InlineData("frame", true)]
        [InlineData("flush", false)]
        [InlineData("flush", true)]
        public void Buffering_log_stream_that_tears_a_frame_loses_no_acknowledged_commit(string tear, bool ioFailure)
        {
            using var data = new MemoryStream();
            using var device = new TornLog { IoFailure = ioFailure };
            var holding = tear == "buffered" ? null : new HoldingLog(device, tear);
            var acknowledged = Enumerable.Range(1, 20).ToList();
            (byte[] Data, byte[] Log) image;
            bool torn;
            using (var log = holding ?? (Stream)new BufferedStream(device, 65536))
            using (var engine = new LiteEngine(new EngineSettings { DataStream = data, LogStream = log, TransactionPageLimit = 1 }))
            using (var db = new LiteDatabase(engine, disposeOnClose: false))
            {
                db.CheckpointSize = 0;
                db.GetCollection("rows").Insert(acknowledged.Select(id => Row(id, 0)));

                // Safepoints write the insert's pages before its commit.
                if (holding != null) holding.Armed = true;
                else device.Arm(1, completeFrame: false, failSetLength: false);
                try
                {
                    db.GetCollection("rows").Insert(Enumerable.Range(100, 30).Select(id => Row(id, 0)));
                    acknowledged.AddRange(Enumerable.Range(100, 30));
                }
                catch (Exception) { }
                torn = device.Torn;
                device.Disarm();

                foreach (var id in new[] { 200, 201 })
                {
                    try
                    {
                        db.GetCollection("rows").Insert(Row(id, 0));
                        acknowledged.Add(id);
                    }
                    // A refused write is not acknowledged: after a recorded write failure the engine
                    // continues read-only (decision 6 of docs/decisions/durability-policy.md); after a
                    // safepoint's failed I/O it stays closed instead (a suspected defect, see
                    // Issue2821_Tests.Disk_full_safepoint_write_leaves_the_engine_read_only).
                    catch (Exception ex) when (ex.Message.StartsWith(LiteEngine.WriteFailedPrefix) || ex.Message.StartsWith("Engine closed")) { }
                }
                // A killed process loses what the stream holds; the device keeps what reached it.
                image = (data.ToArray(), device.ToArray());
            }

            using var recoveredData = new MemoryStream(image.Data);
            using var recoveredLog = new MemoryStream(image.Log);
            using var recovered = new LiteDatabase(new LiteEngine(new EngineSettings { DataStream = recoveredData, LogStream = recoveredLog }));
            recovered.GetCollection("rows").FindAll().Select(x => x["_id"].AsInt32)
                .Should().BeEquivalentTo(acknowledged, "every acknowledged commit survives, the failed one is absent");
            torn.Should().Be(tear != "frame", "a frame is written on at the flush after its own write");
            acknowledged.Contains(100).Should().Be(tear == "frame", "only a torn frame fails the insert");
        }

        private static void WriteLater(LiteDatabase db)
        {
            var rows = db.GetCollection("rows");
            rows.Update(Enumerable.Range(1, 20).Select(id => Row(id, 7))).Should().Be(20);
            rows.Insert(Row(200, 0));
            rows.Count().Should().Be(21);
        }

        /// <summary>A killed process leaves every byte the streams hold.</summary>
        private static void AssertRecovered(MemoryStream data, MemoryStream log)
        {
            using var recoveredData = new MemoryStream(data.ToArray());
            using var recoveredLog = new MemoryStream(log.ToArray());
            using var recovered = new LiteDatabase(new LiteEngine(new EngineSettings { DataStream = recoveredData, LogStream = recoveredLog }));
            var docs = recovered.GetCollection("rows").FindAll().ToList();
            docs.Select(x => x["_id"].AsInt32).Should().BeEquivalentTo(Enumerable.Range(1, 20).Concat(new[] { 200 }),
                "every acknowledged commit survives, and the failed insert is absent");
            docs.Where(x => x["_id"].AsInt32 <= 20).Should().OnlyContain(x => x["value"].AsInt32 == 7);
        }

        private static BsonDocument Row(int id, int value) => new BsonDocument
        {
            ["_id"] = id, ["value"] = value, ["payload"] = new string('p', 500)
        };

        /// <summary>
        /// A caller log stream that holds its latest write and writes it on to the device at the
        /// next write or at Flush. Armed, it tears the first frame it writes on at the next frame's
        /// write ("frame") or at a flush or padding write ("flush"), through the device's tear.
        /// </summary>
        private sealed class HoldingLog : Stream
        {
            private readonly TornLog _device;
            private readonly string _tearAt;
            private byte[] _held;
            private long _heldAt, _position;
            internal bool Armed;

            internal HoldingLog(TornLog device, string tearAt)
            {
                _device = device;
                _tearAt = tearAt;
            }

            public override bool CanRead => true;
            public override bool CanSeek => true;
            public override bool CanWrite => true;
            public override long Length => _held == null ? _device.Length : Math.Max(_device.Length, _heldAt + _held.Length);
            public override long Position { get => _position; set => _position = value; }

            public override long Seek(long offset, SeekOrigin origin) => _position =
                origin == SeekOrigin.Begin ? offset : origin == SeekOrigin.Current ? _position + offset : this.Length + offset;

            public override void Write(byte[] buffer, int offset, int count)
            {
                this.WriteOn(count == WalChecksum.FrameSize ? "frame" : "flush");
                _held = new byte[count];
                Buffer.BlockCopy(buffer, offset, _held, 0, count);
                _heldAt = _position;
                _position += count;
            }

            public override void Flush() => this.WriteOn("flush");

            public override int Read(byte[] buffer, int offset, int count)
            {
                this.WriteOn("flush");
                _device.Position = _position;
                var read = _device.Read(buffer, offset, count);
                _position += read;
                return read;
            }

            public override void SetLength(long value)
            {
                this.WriteOn("flush");
                _device.SetLength(value);
            }

            /// <summary>The held bytes are gone once written on, torn or not.</summary>
            private void WriteOn(string at)
            {
                if (_held == null) return;
                var held = _held;
                _held = null;
                if (Armed && at == _tearAt && held.Length == WalChecksum.FrameSize)
                {
                    Armed = false;
                    _device.Arm(1, completeFrame: false, failSetLength: false);
                }
                _device.Position = _heldAt;
                _device.Write(held, 0, held.Length);
            }
        }

        /// <summary>
        /// A log stream whose n-th frame write after arming stores half the frame (or all of it) and
        /// then fails, and whose SetLength then fails while armed. The failure is an IOException or,
        /// for a caller stream, any other exception.
        /// </summary>
        private sealed class TornLog : MemoryStream
        {
            private int _tearFrame;
            private bool _completeFrame, _failSetLength;
            internal bool IoFailure, Torn, SetLengthFailed;

            internal void Arm(int frame, bool completeFrame, bool failSetLength)
            {
                _tearFrame = frame;
                _completeFrame = completeFrame;
                _failSetLength = failSetLength;
            }

            internal void Disarm() => _tearFrame = 0;

            public override void Write(byte[] buffer, int offset, int count)
            {
                if (_tearFrame > 0 && count == WalChecksum.FrameSize && --_tearFrame == 0)
                {
                    base.Write(buffer, offset, _completeFrame ? count : count / 2);
                    Torn = true;
                    throw Failure("injected torn frame write");
                }
                base.Write(buffer, offset, count);
            }

            public override void SetLength(long value)
            {
                if (_failSetLength && Torn && !SetLengthFailed)
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
