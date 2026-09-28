#if DEBUG || TESTING
using System;
using System.IO;
using System.Linq;
using FluentAssertions;
using LiteDB.Engine;
using Xunit;

namespace LiteDB.Tests.Regressions
{
    /// <summary>
    /// A sync that fails with an I/O error ("fsyncgate"): Linux marks the pages it could not write
    /// back clean, so they stay in the page cache without reaching the device, and a later sync of
    /// the same file succeeds without writing them. Recovery must not take such bytes as durable.
    /// Here a checkpoint's salt rotation wrote the new header, whose sync failed: the WAL and its
    /// header journal are kept, and the next open reads the new header from the cache. Retiring the
    /// journal behind a sync that writes nothing left the old header on the device, and a power
    /// loss then discarded every later commit as a stale WAL generation. Recovery now writes the
    /// header back before the sync that retires its journal (external review, point 3).
    /// </summary>
    [Trait("Category", "IoSafety")]
    public class SyncFailureRecovery_Tests
    {
        [Fact]
        public void Journal_retired_after_a_failed_header_sync_leaves_the_header_on_the_device()
        {
            using var dataFile = new TempFile();
            using var logFile = new TempFile();
            using var data = new ForgetfulFile(dataFile.Filename);
            using var log = new ForgetfulFile(logFile.Filename);
            var settings = new EngineSettings
            {
                DataStream = data, LogStream = log,
                CheckpointStage = stage => { if (stage == "before-reclaim") data.FailNextSync = true; }
            };
            using (var engine = new LiteEngine(settings))
            using (var db = new LiteDatabase(engine, disposeOnClose: false))
            {
                db.CheckpointSize = 0;
                db.GetCollection("rows").Insert(Enumerable.Range(1, 10).Select(Row));
                Action checkpoint = () => db.Checkpoint();
                checkpoint.Should().Throw<IOException>("the salt rotation's sync failed");
            }
            data.Durable.Take(Constants.PAGE_SIZE).Should().NotEqual(data.Live.Take(Constants.PAGE_SIZE),
                "the rotated header is in the page cache only, and no later sync writes it");

            settings.CheckpointStage = null;
            using (var engine = new LiteEngine(settings))
            using (var db = new LiteDatabase(engine, disposeOnClose: false))
            {
                db.CheckpointSize = 0;
                db.GetCollection("rows").Count().Should().Be(10);
                db.GetCollection("rows").Insert(Enumerable.Range(11, 5).Select(Row));
                db.Execute("SELECT $ FROM $database").Single()["durableLogFlush"].AsBoolean.Should().BeTrue();
            }

            var header = (Durable: data.Durable.Take(Constants.PAGE_SIZE).ToArray(), Live: data.Live.Take(Constants.PAGE_SIZE).ToArray());
            using var image = new TempFile();
            using var imageLog = new TempFile();
            File.WriteAllBytes(image.Filename, data.Durable);
            File.WriteAllBytes(imageLog.Filename, log.Durable);
            using var afterPowerLoss = new LiteEngine(new EngineSettings
            {
                DataStream = new FileStream(image.Filename, FileMode.Open, FileAccess.ReadWrite),
                LogStream = new FileStream(imageLog.Filename, FileMode.Open, FileAccess.ReadWrite)
            });
            afterPowerLoss.Query("rows", Query.All()).ToList().Select(x => x["_id"].AsInt32)
                .Should().Equal(Enumerable.Range(1, 15), "every commit acknowledged durable survives the power loss");
            header.Durable.Should().Equal(header.Live, "recovery wrote the header back before the sync that retired its journal");
        }

        private static BsonDocument Row(int id) => new BsonDocument { ["_id"] = id, ["payload"] = new string('x', 500) };
    }
}
#endif
