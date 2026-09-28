#if DEBUG || TESTING
using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using FluentAssertions;
using LiteDB.Engine;
using LiteDB.Internals;
using LiteDB.Tests.Issues;
using Xunit;
using static LiteDB.Constants;

namespace LiteDB.Tests.Regressions
{
    /// <summary>
    /// A data sync that runs outside the WAL writer's lock (DiskService.DataFileSyncs before the engine
    /// created its WAL writer: a `$database` read, a rebuild's check, an index migration) shares the
    /// data writer with a checkpoint's header steps. Every positioned access to that writer holds its
    /// lock. Where the data file cannot sync, a compact write does not retry its refused promotion
    /// (and the promotion's data sync) for every document.
    /// </summary>
    [Trait("Category", "IoSafety")]
    [Collection(NativeFileSyncCollection.Name)]
    public class DataSyncConcurrency_Tests
    {
        private const int Rows = 64;

        /// <summary>
        /// A data sync on another thread is paused inside its header read while a full checkpoint
        /// rotates the WAL salt. The salt rotation's header read and write took only the WAL writer's
        /// lock, so they moved the shared data writer under the paused read: the checkpoint failed
        /// ("The stream ended before the requested data was read") and stopped the engine, and a new
        /// header could land at another offset. The rotation now waits for the data writer's lock; the
        /// checkpoint completes and the database reopens with every row.
        /// </summary>
        [Fact]
        public void Data_sync_outside_the_wal_writer_lock_waits_for_the_salt_rotation()
        {
            using var file = new TempFile();
            using (var setup = new LiteDatabase(file.Filename))
                setup.GetCollection("rows").Insert(Enumerable.Range(1, Rows).Select(id => MvccRetirementScenario.Document(id, 0)));
            var logName = FileHelper.GetLogFile(file.Filename);
            Thread sync = null;
            Exception syncError = null;
            var armed = false;
            using (var data = new GateFile(file.Filename))
            using (var log = new FileStream(logName, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete, 1))
            {
                LiteEngine engine = null;
                var settings = new EngineSettings
                {
                    Filename = file.Filename, DataStream = data, LogStream = log,
                    CheckpointStage = stage =>
                    {
                        if (!armed || stage != "before-reclaim") return;
                        armed = false;
                        var disk = (DiskService)typeof(LiteEngine).GetField("_disk", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(engine);
                        sync = new Thread(() =>
                        {
                            try { typeof(DiskService).GetMethod("SyncDataFile", BindingFlags.NonPublic | BindingFlags.Instance).Invoke(disk, null); }
                            catch (Exception ex) { syncError = ex; }
                        });
                        data.Armed = sync;
                        sync.Start();
                        data.InRead.Wait(5000).Should().BeTrue("the sync reached its header read");
                    }
                };
                engine = new LiteEngine(settings);
                using (var db = new LiteDatabase(engine, disposeOnClose: false))
                {
                    db.CheckpointSize = 0;
                    for (var value = 1; value <= 3; value++) Update(db, value);
                    armed = true;
                    Action checkpoint = () => db.Checkpoint();
                    checkpoint.Should().NotThrow();
                    sync.Join();
                    syncError.Should().BeNull();
                    SyncPowerLossModel.AssertRows(db, Rows, 3);
                }
                engine.Dispose();
            }
            using var reopened = new LiteDatabase(file.Filename);
            SyncPowerLossModel.AssertRows(reopened, Rows, 3);
        }

        /// <summary>
        /// While the data file cannot sync, every compact-eligible document (nested documents skip the
        /// admission back-off) retried the refused v12 promotion: a header read and a data sync per
        /// document (401 for 400). An engine that knows its data file cannot sync skips the attempt; the
        /// documents are written as BSON and the file keeps its version.
        /// </summary>
        [Fact]
        public void Refused_compact_promotion_is_not_retried_for_every_document()
        {
            using var file = new TempFile();
            using (var setup = new LiteDatabase(new LiteEngine(new EngineSettings { Filename = file.Filename, CompactStorage = CompactStorageMode.Legacy })))
            {
                setup.GetCollection("rows").Insert(Enumerable.Range(1, Rows).Select(id => MvccRetirementScenario.Document(id, 0)));
                setup.Checkpoint();
            }
            using var power = new SyncPowerLossModel(file.Filename);
            var settings = power.Settings();
            settings.CompactStorage = CompactStorageMode.Auto;
            using var db = new LiteDatabase(new LiteEngine(settings));
            power.DataFails = true;
            var before = power.DataSyncs;
            const int documents = 400;
            db.GetCollection("compact").Insert(Enumerable.Range(1, documents).Select(Compact));
            (power.DataSyncs - before).Should().BeLessThan(5, "the promotion is not retried for every document");
            db.GetCollection("compact").FindAll().Should().BeEquivalentTo(Enumerable.Range(1, documents).Select(Compact), o => o.WithStrictOrdering());
            SyncPowerLossModel.ReadShared(file.Filename).Take(PAGE_SIZE).ToArray()[HeaderPage.P_FILE_VERSION]
                .Should().BeLessThan(HeaderPage.COMPACT_FILE_VERSION);
        }

        private static void Update(LiteDatabase db, int value) =>
            db.GetCollection("rows").Upsert(Enumerable.Range(1, Rows).Select(id => MvccRetirementScenario.Document(id, value)));

        private static BsonDocument Compact(int id) => new BsonDocument
        {
            ["_id"] = id, ["longRepeatedFieldName"] = id, ["anotherLongRepeatedFieldName"] = "payload",
            ["nestedDocument"] = new BsonDocument { ["longNestedFieldName"] = id, ["anotherNestedFieldName"] = true },
            ["arrayValues"] = new BsonArray(Enumerable.Range(1, 30).Select(x => new BsonValue(x)))
        };

        /// <summary>A data file whose next read on the armed thread pauses, so another thread's header I/O runs meanwhile.</summary>
        private sealed class GateFile : FileStream
        {
            internal volatile Thread Armed;
            internal readonly ManualResetEventSlim InRead = new ManualResetEventSlim();

            internal GateFile(string path)
                : base(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete, 1) { }

            public override int Read(byte[] buffer, int offset, int count)
            {
                if (Armed != null && Armed == Thread.CurrentThread)
                {
                    Armed = null;
                    InRead.Set();
                    Thread.Sleep(500);
                }
                return base.Read(buffer, offset, count);
            }

            protected override void Dispose(bool disposing)
            {
                if (disposing) InRead.Dispose();
                base.Dispose(disposing);
            }
        }
    }
}
#endif
