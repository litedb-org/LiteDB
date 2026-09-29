#if DEBUG || TESTING
using System;
using System.IO;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Threading;
using FluentAssertions;
using LiteDB.Engine;
using LiteDB.Internals;
using LiteDB.Tests.Issues;
using Xunit;

namespace LiteDB.Tests.Regressions
{
    /// <summary>
    /// A checkpoint writes only to a data file that just synced (DiskService.KeepsWal). Where the data
    /// file cannot sync (#2242), a checkpoint that already knows so retries the data sync before it
    /// takes a lock or scans the growing WAL; on storage that syncs, the rule costs a rationed or
    /// blocked checkpoint nothing. <c>$database.walKept</c> tries the data sync only where it can.
    /// </summary>
    [Trait("Category", "IoSafety")]
    [Collection(NativeFileSyncCollection.Name)]
    public class CheckpointDataSync_Tests
    {
        private const int Rows = 64;

        /// <summary>
        /// A shared connection whose checkpoint found that the data file cannot sync: every later
        /// operation's engine is fresh, and each of its checkpoints scanned the WAL again before its
        /// data sync failed. The connection's finding now lets them retry the data sync first and stop
        /// there, until a data sync succeeds.
        /// </summary>
        [Fact]
        public void Shared_connection_checkpoints_stop_at_the_data_sync_once_it_failed()
        {
            using var file = new TempFile();
            Setup(file.Filename);
            using var power = new SyncPowerLossModel(file.Filename);
            var settings = power.Settings();
            var scans = 0;
            settings.CheckpointStage = stage => { if (stage == "before-commit-lock") Interlocked.Increment(ref scans); };
            using var engine = new SharedEngine(settings);
            using var db = new LiteDatabase(engine, disposeOnClose: false);
            db.CheckpointSize = 0;
            Update(db, 1);
            power.DataFails = true;
            db.Checkpoint();
            scans.Should().Be(1, "the first checkpoint found out at its pre-write sync");
            for (var value = 2; value <= 4; value++)
            {
                Update(db, value);
                db.Checkpoint();
            }
            scans.Should().Be(1, "later operations' checkpoints stopped at their data sync");
            SyncPowerLossModel.AssertRows(db, Rows, 4);
            db.GetCollection("$database").FindAll().Single()["walKept"].AsBoolean.Should().BeTrue();

            power.DataFails = false;
            db.Checkpoint();
            scans.Should().Be(2, "the data file syncs again");
            db.GetCollection("$database").FindAll().Single()["logFileSize"].AsInt64.Should().Be(0);
            power.AssertAfterPowerLoss(Rows, 4);
        }

        private static void Setup(string filename)
        {
            using var setup = new LiteDatabase(filename);
            setup.GetCollection("rows").Insert(Enumerable.Range(1, Rows).Select(id => MvccRetirementScenario.Document(id, 0)));
            setup.GetCollection("rows").EnsureIndex("value");
        }

        private static void Update(LiteDatabase db, int value) =>
            db.GetCollection("rows").Upsert(Enumerable.Range(1, Rows).Select(id => MvccRetirementScenario.Document(id, value)));

    }
}
#endif
