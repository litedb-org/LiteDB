#if DEBUG || TESTING
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using FluentAssertions;
using LiteDB.Engine;
using Xunit;

namespace LiteDB.Tests.Regressions
{
    /// <summary>
    /// The WAL writer and the readers share a caller log stream, each through its own wrapper. A
    /// BufferedStream (or a FileStream with a large buffer) held the frame the writer had just
    /// written until the writer's next access, and a reader's seek wrote it on instead, on the
    /// reader's thread: a failure tore it there and the writer never saw it (a non-I/O failure
    /// stopped nothing), the BufferedStream wrote the frame again further on at the writer's next
    /// access, and the commit was acknowledged and lost at recovery (31 of 32 rows). Such a stream
    /// is now flushed after each write, under the lock the readers take, so nothing is held for a
    /// reader to write on. The reader runs in the window after a frame's write returned.
    /// </summary>
    [Trait("Category", "IoSafety")]
    public class SharedBufferedLogStream_Tests
    {
        [Theory]
        [InlineData("wal-page-after-write", false)]
        [InlineData("wal-page-after-write", true)]
        [InlineData("wal-confirmation-after-write", false)]
        [InlineData("wal-confirmation-after-write", true)]
        public void Reader_after_a_frame_write_writes_nothing_on(string point, bool ioFailure)
        {
            using var data = new MemoryStream();
            using var device = new TearingDevice { IoFailure = ioFailure };
            using var log = new BufferedStream(device, 1 << 20);
            using (var db = new LiteDatabase(new LiteEngine(new EngineSettings { DataStream = data, LogStream = log })))
            {
                db.CheckpointSize = 0;
                db.GetCollection("a").Insert(Enumerable.Range(1, 40).Select(Row));
                db.GetCollection("b").Insert(Row(0));
            }

            var acknowledged = new List<int> { 0 };
            Exception readerFailure = null;
            var readerReadLog = false;
            byte[] imageData, imageLog;
            using (var engine = new LiteEngine(new EngineSettings { DataStream = data, LogStream = log, TransactionPageLimit = 4 }))
            using (var db = new LiteDatabase(engine, disposeOnClose: false))
            {
                Thread reader = null;
                engine.BeforePageRead = (position, origin) =>
                {
                    if (origin == FileOrigin.Log && Thread.CurrentThread == reader) readerReadLog = true;
                };
                engine.SimulateCrashPoint = phase =>
                {
                    if (phase != point || reader != null) return;
                    device.Armed = true;
                    reader = new Thread(() =>
                    {
                        try { db.GetCollection("a").FindAll().ToList(); }
                        catch (Exception ex) { readerFailure = ex; }
                    });
                    reader.Start();
                    if (!reader.Join(TimeSpan.FromSeconds(30))) readerFailure = new TimeoutException("the reader was blocked");
                    device.Armed = false;
                };
                foreach (var ids in new[] { Enumerable.Range(1, 30).ToArray(), new[] { 100 } })
                {
                    try
                    {
                        db.GetCollection("b").Insert(ids.Select(Row));
                        acknowledged.AddRange(ids);
                    }
                    catch (Exception) { }
                }
                reader.Should().NotBeNull("the writer reached " + point);
                // A killed process loses what the stream holds; the device keeps what reached it.
                imageData = data.ToArray();
                imageLog = device.ToArray();
            }

            using (var recovered = new LiteDatabase(new LiteEngine(new EngineSettings
            {
                DataStream = new MemoryStream(imageData), LogStream = new MemoryStream(imageLog)
            })))
            {
                recovered.GetCollection("a").Count().Should().Be(40);
                recovered.GetCollection("b").FindAll().Select(x => x["_id"].AsInt32).Should().BeEquivalentTo(acknowledged,
                    "every acknowledged commit survives (reader failure: {0})", readerFailure?.GetType().Name);
            }
            readerReadLog.Should().BeTrue("the reader read the WAL in the window");
            readerFailure.Should().BeNull("nothing was held for the reader's seek to write on");
            acknowledged.Should().HaveCount(32);
        }

        private static BsonDocument Row(int id) => new BsonDocument { ["_id"] = id, ["payload"] = new string('p', 1500) };

        /// <summary>Armed, the next write of at least a frame stores half of it and fails.</summary>
        private sealed class TearingDevice : MemoryStream
        {
            internal volatile bool Armed;
            internal bool IoFailure;

            public override void Write(byte[] buffer, int offset, int count)
            {
                if (Armed && count >= WalChecksum.FrameSize)
                {
                    Armed = false;
                    base.Write(buffer, offset, count / 2);
                    throw IoFailure ? new IOException("injected torn write") : new InvalidOperationException("injected torn write");
                }
                base.Write(buffer, offset, count);
            }
        }
    }
}
#endif
