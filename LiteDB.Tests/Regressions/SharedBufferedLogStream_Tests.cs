#if DEBUG || TESTING
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using FluentAssertions;
using LiteDB.Engine;
using Xunit;

namespace LiteDB.Tests.Regressions
{
    /// <summary>
    /// The WAL writer and the readers share a caller log stream, each through its own wrapper. A
    /// BufferedStream (or a FileStream with a large buffer) holds the frame the writer has just
    /// written until the writer's next access, and a reader's seek writes it on instead, on the
    /// reader's thread: a failure tore it there and the writer never saw it (a non-I/O failure
    /// stopped nothing), the BufferedStream wrote the frame again further on at the writer's next
    /// access, and the commit was acknowledged and lost at recovery (31 of 32 rows). A reader's
    /// access now first flushes what the stream holds for the writer (HeldWrites): a failure there,
    /// also one that reads like "cannot sync" (that flush only writes), fails the read as a write
    /// failure of the log and is handed to the writer, whose batch fails and stops the engine; a
    /// cold reopen recovers exactly the acknowledged commits. Without a failure the commit goes on.
    /// Nothing is flushed per write (CallerStreamFlushCost_Tests). The reader runs in the window
    /// after a frame's write returned.
    /// </summary>
    [Trait("Category", "IoSafety")]
    public class SharedBufferedLogStream_Tests
    {
        [Theory]
        [InlineData("wal-page-after-write", "none")]
        [InlineData("wal-page-after-write", "io")]
        [InlineData("wal-page-after-write", "other")]
        [InlineData("wal-page-after-write", "unauthorized")]
        [InlineData("wal-page-after-write", "einval")]
        [InlineData("wal-confirmation-after-write", "none")]
        [InlineData("wal-confirmation-after-write", "io")]
        [InlineData("wal-confirmation-after-write", "other")]
        [InlineData("wal-confirmation-after-write", "unauthorized")]
        [InlineData("wal-confirmation-after-write", "einval")]
        public void Reader_that_writes_a_held_frame_on_hands_its_failure_to_the_writer(string point, string failure)
        {
            using var data = new MemoryStream();
            using var device = new TearingDevice { Failure = failure };
            using var log = new BufferedStream(device, 1 << 20);
            using (var db = new LiteDatabase(new LiteEngine(new EngineSettings { DataStream = data, LogStream = log })))
            {
                db.CheckpointSize = 0;
                db.GetCollection("a").Insert(Enumerable.Range(1, 40).Select(Row));
                db.GetCollection("b").Insert(Row(0));
            }

            var acknowledged = new List<int> { 0 };
            Exception readerFailure = null, writerFailure = null;
            List<int> readAfter = null;
            Exception readAfterFailure = null;
            BsonDocument info = null;
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
                    device.Armed = failure != "none";
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
                    catch (Exception ex) { writerFailure ??= ex; }
                }
                reader.Should().NotBeNull("the writer reached " + point);
                // Without a failure the engine goes on; after one it stopped and throws the failure.
                try
                {
                    readAfter = db.GetCollection("b").FindAll().Select(x => x["_id"].AsInt32).ToList();
                    info = db.Execute("SELECT $ FROM $database").Single().AsDocument;
                }
                catch (Exception ex) { readAfterFailure = ex; }
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
            if (failure == "none")
            {
                readAfterFailure.Should().BeNull();
                readAfter.Should().BeEquivalentTo(acknowledged, "the engine reads what was acknowledged");
                readerFailure.Should().BeNull("the reader wrote the held frame on whole");
                writerFailure.Should().BeNull();
                acknowledged.Should().HaveCount(32);
                info["readOnly"].AsBoolean.Should().BeFalse();
                return;
            }
            readerFailure.Should().BeOfType(TearingDevice.ExceptionType(failure));
            readerFailure.Message.Should().Be("injected torn write", "the reader's seek wrote the held frame on and tore it");
            writerFailure.Should().NotBeNull("the writer's batch failed");
            // A failure that is no IOException reaches the writer only by the hand-off. An I/O failure
            // also stops the engine from the reader's own call (recorded as a write failure of the log),
            // which the writer's batch can meet first; either way it fails inside the batch and stops.
            if (!(readerFailure is IOException))
                writerFailure.ToString().Should().Contain("failed when another access wrote it on: injected torn write");
            acknowledged.Should().Equal(new[] { 0 }, "the torn batch failed and the stopped engine refuses later writes");
            readAfterFailure.Should().BeOfType<IOException>("the engine stopped after the write failure")
                .Which.Message.Should().StartWith("Engine closed after an I/O failure");
            readAfterFailure.ToString().Should().Contain("injected torn write", "the stopped engine throws the original failure");
        }

        private static BsonDocument Row(int id) => new BsonDocument { ["_id"] = id, ["payload"] = new string('p', 1500) };

        /// <summary>
        /// Armed, the next write of at least a frame stores half of it and fails: an I/O error, a
        /// non-I/O failure, or one that reads like "cannot sync" (EACCES, EINVAL; a write failure here).
        /// </summary>
        private sealed class TearingDevice : MemoryStream
        {
            internal volatile bool Armed;
            internal string Failure;

            internal static Type ExceptionType(string failure) =>
                failure == "other" ? typeof(InvalidOperationException) :
                failure == "unauthorized" ? typeof(UnauthorizedAccessException) : typeof(IOException);

            public override void Write(byte[] buffer, int offset, int count)
            {
                if (Armed && count >= WalChecksum.FrameSize)
                {
                    Armed = false;
                    base.Write(buffer, offset, count / 2);
                    const string message = "injected torn write";
                    switch (Failure)
                    {
                        case "other": throw new InvalidOperationException(message);
                        case "unauthorized": throw new UnauthorizedAccessException(message);
                        case "einval":
                            throw new IOException(message, RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? unchecked((int)0x80070032) : 22);
                        default: throw new IOException(message);
                    }
                }
                base.Write(buffer, offset, count);
            }
        }
    }
}
#endif
