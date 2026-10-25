using System;
using System.IO;
using System.Linq;
using FluentAssertions;
using LiteDB.Engine;
using LiteDB.Tests;
using Xunit;

namespace LiteDB.Internals
{
    /// <summary>
    /// #2242 in shared mode: every operation opens a fresh engine whose recovery registers
    /// the WAL slots an earlier engine cleared without a durable sync. Those slots must
    /// never be reused, a live reader keeps its snapshot, and the connection keeps
    /// reporting the weaker guarantee. Each fresh engine still makes one real sync
    /// attempt per write: its reuse probe, after which its commit flushes without one.
    /// </summary>
    public class SharedUnsyncableLog_Tests
    {
        private const int DocumentCount = 64; // Streams past the shared buffered-result budget.
        private const int LaterWrites = 5;

        [Fact]
        public void Fresh_shared_engines_never_reuse_slots_cleared_on_a_log_that_cannot_sync()
        {
            using var file = new TempFile();
            using var data = new SyncFile(file.Filename);
            using var log = new SyncFile(file.Filename + "-wal") { Failure = new UnauthorizedAccessException("sync unsupported") };
            using var engine = new SharedEngine(new EngineSettings
            {
                Filename = file.Filename, DataStream = data, LogStream = log,
                CompactStorage = CompactStorageMode.Legacy, TransactionPageLimit = 1
            });
            using var db = new LiteDatabase(engine, disposeOnClose: false);
            // No automatic or close checkpoints: every rejected sync below is a commit or a probe.
            db.CheckpointSize = 0;
            Write(db, "cold", 0);
            for (var value = 0; value <= 20; value++) Write(db, "docs", value);

            using var reader = engine.Query("docs", new Query());
            reader.Read().Should().BeTrue("the first read registers the reader's lease");
            reader.Current["value"].AsInt32.Should().Be(20);
            int[] cleared = null;
            MvccCheckpoint_Tests.RunThread(() =>
            {
                db.Checkpoint();
                cleared = BlankFrames(ReadAll(log));
            });
            cleared.Should().NotBeEmpty("the checkpoint reclaims versions the live reader does not need");
            IsDurable(db).Should().BeFalse();

            var rejectedBefore = log.RejectedSyncs;
            MvccCheckpoint_Tests.RunThread(() =>
            {
                for (var value = 1; value <= LaterWrites; value++) Write(db, "cold", value);
            });

            var written = ReadAll(log);
            cleared.Count(offset => !IsBlank(written, offset)).Should().Be(0,
                "slots cleared without a durable sync must not be reused by later engines");
            (log.RejectedSyncs - rejectedBefore).Should().Be(LaterWrites,
                "every fresh engine retries a real sync once, and no more than once, per write");
            IsDurable(db).Should().BeFalse("the connection keeps reporting the weaker guarantee across reopens");

            var count = 1;
            while (reader.Read())
            {
                reader.Current["value"].AsInt32.Should().Be(20, "the reader keeps its snapshot");
                count++;
            }
            count.Should().Be(DocumentCount);

            // A killed process leaves every byte in the OS cache; recover that image.
            using var dataCopy = new MemoryStream(ReadAll(data));
            using var logCopy = new MemoryStream(written);
            using var recovered = new LiteDatabase(new LiteEngine(new EngineSettings
            {
                DataStream = dataCopy, LogStream = logCopy, CompactStorage = CompactStorageMode.Legacy
            }));
            recovered.GetCollection("cold").FindAll().Should().HaveCount(DocumentCount)
                .And.OnlyContain(doc => doc["value"].AsInt32 == LaterWrites);
            recovered.GetCollection("docs").FindAll().Should().OnlyContain(doc => doc["value"].AsInt32 == 20);
        }

        private static void Write(LiteDatabase db, string collection, int value)
        {
            db.GetCollection(collection).Upsert(Enumerable.Range(0, DocumentCount).Select(id =>
                new BsonDocument { ["_id"] = id, ["value"] = value, ["payload"] = new string('x', 1500) }));
        }

        private static bool IsDurable(LiteDatabase db) =>
            db.GetCollection("$database").FindAll().Single()["durableLogFlush"].AsBoolean;

        private static byte[] ReadAll(Stream stream)
        {
            lock (stream)
            {
                var position = stream.Position;
                var bytes = new byte[stream.Length];
                stream.Position = 0;
                var read = 0;
                while (read < bytes.Length) read += stream.Read(bytes, read, bytes.Length - read);
                stream.Position = position;
                return bytes;
            }
        }

        private static int[] BlankFrames(byte[] log) => Enumerable.Range(0, log.Length / WalChecksum.FrameSize)
            .Select(frame => frame * WalChecksum.FrameSize).Where(offset => IsBlank(log, offset)).ToArray();

        private static bool IsBlank(byte[] log, int offset) =>
            log.Skip(offset).Take(WalChecksum.FrameSize).All(value => value == 0);

        private sealed class SyncFile : FileStream
        {
            internal Exception Failure;
            internal int RejectedSyncs;

            internal SyncFile(string filename)
                : base(filename, FileMode.Create, FileAccess.ReadWrite, FileShare.ReadWrite,
                    4096, FileOptions.DeleteOnClose) { }

            public override void Flush(bool flushToDisk)
            {
                if (flushToDisk && Failure != null)
                {
                    base.Flush(false);
                    RejectedSyncs++;
                    throw Failure;
                }
                base.Flush(flushToDisk);
            }
        }
    }
}
