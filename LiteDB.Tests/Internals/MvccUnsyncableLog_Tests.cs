using System;
using System.IO;
using System.Linq;
using FluentAssertions;
using LiteDB.Engine;
using Xunit;

namespace LiteDB.Internals
{
    /// <summary>
    /// #2242 under MVCC: a log that rejects every device sync keeps snapshot checkpoints,
    /// retirement records and readers working, but never reuses reclaimed WAL slots.
    /// Without a durable clear, a reused slot could overwrite a retired version that
    /// unsynced data pages still depend on after a power loss, so the WAL appends like dev.
    /// </summary>
    public class MvccUnsyncableLog_Tests
    {
        private const int DocumentCount = 16;

        [Fact]
        public void Snapshot_checkpoint_on_a_log_that_never_syncs_appends_instead_of_reusing_slots()
        {
            using var data = new MemoryStream();
            using var log = new UnsyncableLog();
            using (var engine = Open(data, log))
            using (var db = new LiteDatabase(engine, disposeOnClose: false))
            {
                db.Pragma(Pragmas.CHECKPOINT, 0);
                Write(db, "cold", 0);
                Write(db, "docs", 0);
                for (var value = 1; value <= 20; value++) Write(db, "docs", value);

                using var reader = engine.Query("docs", new Query());
                var pinned = engine.GetWalIndex().SnapshotPositions(engine.ReadVersion);
                var before = log.ToArray();
                MvccCheckpoint_Tests.RunThread(() =>
                {
                    engine.Checkpoint();
                    var checkpointed = log.Length;
                    for (var value = 21; value <= 25; value++) Write(db, "cold", value);

                    // With slot reuse only the five confirmation frames would append.
                    ((log.Length - checkpointed) / WalChecksum.FrameSize).Should().BeGreaterThan(5,
                        "reclaimed slots must not be reused on storage that cannot sync");
                    var after = log.ToArray();
                    foreach (var position in pinned)
                    {
                        var offset = (int)(position / Constants.PAGE_SIZE * WalChecksum.FrameSize);
                        after.Skip(offset).Take(Constants.PAGE_SIZE).Should().Equal(before.Skip(offset).Take(Constants.PAGE_SIZE));
                    }
                });

                var count = 0;
                while (reader.Read())
                {
                    reader.Current["value"].AsInt32.Should().Be(20, "the reader keeps its snapshot");
                    count++;
                }
                count.Should().Be(DocumentCount);
                db.GetCollection("$database").FindAll().Single()["durableLogFlush"].AsBoolean.Should().BeFalse();
                log.Rejections.Should().BeGreaterThan(0);
            }

            // A killed process leaves every byte in the OS cache; reopen that image.
            AssertValues(data, log, "cold", 25);
            AssertValues(data, log, "docs", 20);
        }

        private static LiteEngine Open(Stream data, Stream log) => new LiteEngine(new EngineSettings
        {
            CompactStorage = CompactStorageMode.Legacy, DataStream = data, LogStream = log, TransactionPageLimit = 1
        });

        private static void Write(LiteDatabase db, string collection, int value)
        {
            db.GetCollection(collection).Upsert(Enumerable.Range(0, DocumentCount).Select(id =>
                new BsonDocument { ["_id"] = id, ["value"] = value, ["payload"] = new string('x', 1500) }));
        }

        private static void AssertValues(MemoryStream data, MemoryStream log, string collection, int value)
        {
            using var dataCopy = Copy(data, new MemoryStream());
            using var logCopy = Copy(log, new UnsyncableLog());
            using (var engine = Open(dataCopy, logCopy))
            using (var db = new LiteDatabase(engine, disposeOnClose: false))
            {
                db.GetCollection(collection).FindAll().Should().HaveCount(DocumentCount)
                    .And.OnlyContain(doc => doc["value"].AsInt32 == value);
                db.Checkpoint();
            }
            using var reopenedEngine = Open(dataCopy, logCopy);
            using var reopened = new LiteDatabase(reopenedEngine, disposeOnClose: false);
            reopened.GetCollection(collection).FindAll().Should().OnlyContain(doc => doc["value"].AsInt32 == value);
        }

        private static T Copy<T>(MemoryStream source, T target) where T : MemoryStream
        {
            var bytes = source.ToArray();
            target.Write(bytes, 0, bytes.Length);
            target.Position = 0;
            return target;
        }

        /// <summary>A log whose storage answers every device sync with ERROR_ACCESS_DENIED.</summary>
        private sealed class UnsyncableLog : MemoryStream, IDurableStream
        {
            internal int Rejections;

            public void FlushToDisk()
            {
                Rejections++;
                throw new UnauthorizedAccessException("Access to the path is denied.");
            }
        }
    }
}
