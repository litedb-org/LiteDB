#if DEBUG || TESTING
using System.IO;
using System.Linq;
using FluentAssertions;
using LiteDB.Engine;
using LiteDB.Internals;
using Xunit;

namespace LiteDB.Tests.Issues
{
    /// <summary>
    /// Without a bindable C library, Unix file syncs fall back to FileStream.Flush(true), which
    /// loses every fsync error. Such a sync is still attempted, but it proves nothing: the log is
    /// reported as not durable and reclaimed WAL slots are never reused, as on storage that
    /// cannot sync (#2242). The fallback is simulated, so this runs on every platform.
    /// </summary>
    [Collection(NativeFileSyncCollection.Name)]
    public class RuntimeSyncDurability_Tests
    {
        private const int DocumentCount = 16;

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void An_unverifiable_log_sync_never_proves_durability_or_allows_slot_reuse(bool runtimeSync)
        {
            using var file = new TempFile();
            var logFile = FileHelper.GetLogFile(file.Filename);
            NativeFileSync.SimulateRuntimeSync = runtimeSync;
            try
            {
                using (var engine = new LiteEngine(new EngineSettings
                {
                    Filename = file.Filename, CompactStorage = CompactStorageMode.Legacy, TransactionPageLimit = 1
                }))
                using (var db = new LiteDatabase(engine, disposeOnClose: false))
                {
                    db.Pragma(Pragmas.CHECKPOINT, 0);
                    Write(db, "cold", 0);
                    Write(db, "docs", 0);
                    for (var value = 1; value <= 20; value++) Write(db, "docs", value);
                    using (engine.Query("docs", new Query()))
                    {
                        // A snapshot checkpoint clears the frames no reader needs.
                        MvccCheckpoint_Tests.RunThread(() => engine.Checkpoint());
                    }

                    var cleared = Read(logFile);
                    for (var value = 21; value <= 25; value++) Write(db, "cold", value);
                    var written = Read(logFile);
                    var reused = BlankFrames(cleared).Count(offset => !IsBlank(written, offset));

                    if (runtimeSync)
                    {
                        reused.Should().Be(0, "an unverifiable sync must not make cleared slots reusable");
                        DurableLogFlush(db).Should().BeFalse("a sync that cannot report failure proves no durability");
                    }
                    else
                    {
                        reused.Should().BeGreaterThan(0, "a verified log reuses the cleared slots");
                        DurableLogFlush(db).Should().BeTrue();
                    }
                }
            }
            finally { NativeFileSync.SimulateRuntimeSync = false; }

            using var reopened = new LiteDatabase(file.Filename);
            reopened.GetCollection("cold").FindAll().Should().OnlyContain(doc => doc["value"].AsInt32 == 25);
            reopened.GetCollection("docs").FindAll().Should().OnlyContain(doc => doc["value"].AsInt32 == 20);
        }

        private static void Write(LiteDatabase db, string collection, int value) =>
            db.GetCollection(collection).Upsert(Enumerable.Range(0, DocumentCount).Select(id =>
                new BsonDocument { ["_id"] = id, ["value"] = value, ["payload"] = new string('x', 1500) }));

        private static byte[] Read(string path)
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var copy = new MemoryStream();
            stream.CopyTo(copy);
            return copy.ToArray();
        }

        private static int[] BlankFrames(byte[] log) => Enumerable.Range(0, log.Length / WalChecksum.FrameSize)
            .Select(frame => frame * WalChecksum.FrameSize).Where(offset => IsBlank(log, offset)).ToArray();

        private static bool IsBlank(byte[] log, int offset) =>
            offset + WalChecksum.FrameSize <= log.Length &&
            log.Skip(offset).Take(WalChecksum.FrameSize).All(value => value == 0);

        private static bool DurableLogFlush(LiteDatabase db) =>
            db.GetCollection("$database").FindAll().Single()["durableLogFlush"].AsBoolean;
    }
}
#endif
