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
    /// Data writes are counted, and the log shrinks only behind a data sync that covered every one.
    /// Where the data file cannot sync, a compact write does not retry its refused promotion (and the
    /// promotion's data sync) for every document.
    /// </summary>
    [Trait("Category", "IoSafety")]
    [Collection(NativeFileSyncCollection.Name)]
    public class DataSyncConcurrency_Tests
    {
        private const int Rows = 64;

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

        /// <summary>
        /// The log shrinks only behind a data sync of this engine that covered every data write it made
        /// (DiskService.ShrinkLog): an engine that has not synced the data file yet (an earlier engine
        /// may have left writes in the OS cache), and one with a data write no sync covered, stop with
        /// "stopped syncing" before the log changes; after a covering data sync the log shrinks.
        /// </summary>
        [Fact]
        public void Log_shrinks_only_behind_a_data_sync_that_covered_every_data_write()
        {
            using var file = new TempFile();
            using (var setup = new LiteDatabase(file.Filename))
                setup.GetCollection("rows").Insert(new BsonDocument { ["_id"] = 1 });
            using var engine = new LiteEngine(new EngineSettings { Filename = file.Filename });
            var disk = (DiskService)typeof(LiteEngine).GetField("_disk", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(engine);
            using var log = new MemoryStream(new byte[100]);

            Action shrink = () => disk.ShrinkLog(log, 50, "a test");
            shrink.Should().Throw<IOException>().WithMessage("The data file stopped syncing*", "this engine has not synced the data file");
            log.Length.Should().Be(100);
            disk.DataFileSyncs().Should().BeTrue();
            shrink.Should().NotThrow();
            log.Length.Should().Be(50);

            typeof(DiskService).GetMethod("CountDataWrite", BindingFlags.NonPublic | BindingFlags.Instance).Invoke(disk, null);
            Action again = () => disk.ShrinkLog(log, 20, "a test");
            again.Should().Throw<IOException>().WithMessage("The data file stopped syncing*", "no data sync covered the latest data write");
            log.Length.Should().Be(50);
            disk.DataFileSyncs().Should().BeTrue();
            again.Should().NotThrow();
            log.Length.Should().Be(20);
        }

        private static void Update(LiteDatabase db, int value) =>
            db.GetCollection("rows").Upsert(Enumerable.Range(1, Rows).Select(id => MvccRetirementScenario.Document(id, value)));

        private static BsonDocument Compact(int id) => new BsonDocument
        {
            ["_id"] = id, ["longRepeatedFieldName"] = id, ["anotherLongRepeatedFieldName"] = "payload",
            ["nestedDocument"] = new BsonDocument { ["longNestedFieldName"] = id, ["anotherNestedFieldName"] = true },
            ["arrayValues"] = new BsonArray(Enumerable.Range(1, 30).Select(x => new BsonValue(x)))
        };
    }
}
#endif
