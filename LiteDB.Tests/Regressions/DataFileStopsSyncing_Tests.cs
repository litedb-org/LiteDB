#if DEBUG || TESTING
using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading;
using FluentAssertions;
using LiteDB.Engine;
using LiteDB.Internals;
using LiteDB.Tests.Issues;
using Xunit;

namespace LiteDB.Tests.Regressions
{
    /// <summary>
    /// A data file that stops syncing (#2242) after commits were acknowledged durable. No log sync
    /// makes an emptied WAL durable before a data sync succeeds (UnsyncedBackfillPowerLoss_Tests),
    /// but that alone does not stop the OS from writing the emptied WAL back ahead of the backfill,
    /// nor a sync outside the engine's barriers. A checkpoint writes only to a data file that just
    /// synced, the WAL is kept until a data sync that covers the backfill succeeds, and a stream the
    /// engine opens to read never syncs.
    /// </summary>
    [Trait("Category", "IoSafety")]
    [Collection(NativeFileSyncCollection.Name)]
    public class DataFileStopsSyncing_Tests
    {
        /// <summary>
        /// The OS writes the WAL back as it is after the full checkpoint, the backfill never reaches
        /// the device: the WAL was emptied, and commits acknowledged durable were lost (every row
        /// back at 0). The checkpoint now syncs the data file first and, as it cannot, writes no
        /// page and keeps the WAL: the data file as last synced with the WAL as written back holds
        /// every row, whole, and the value index finds each. Once the data file syncs, the next
        /// checkpoint empties the WAL.
        /// </summary>
        [Theory]
        [InlineData(false)] // only the data file cannot sync
        [InlineData(true)]  // neither file syncs
        public void Wal_written_back_ahead_of_the_backfill_keeps_every_durable_commit(bool neither)
        {
            using var file = new TempFile();
            Setup(file.Filename);
            using var power = new SyncPowerLossModel(file.Filename);
            using var engine = new LiteEngine(power.Settings());
            using var db = new LiteDatabase(engine, disposeOnClose: false);
            db.CheckpointSize = 0;
            for (var value = 1; value <= 5; value++)
            {
                Update(db, value);
                DurableLogFlush(db).Should().BeTrue();
            }
            power.DataFails = true;
            power.LogFails = neither;
            db.Checkpoint();
            SyncPowerLossModel.ReadShared(file.Filename).Should().Equal(power.Capture().Data, "the checkpoint wrote no page");

            var image = (power.Capture().Data, SyncPowerLossModel.ReadShared(FileHelper.GetLogFile(file.Filename)));
            FilePowerLossModel.Open(image, db => AssertState(db, 5, extra: false));

            power.DataFails = power.LogFails = false;
            db.Checkpoint();
            new FileInfo(FileHelper.GetLogFile(file.Filename)).Length.Should().Be(0, "once the data file syncs, the WAL is emptied");
            AssertState(db, 5, extra: false);
        }

        /// <summary>
        /// The next engine over the same files (a reopen, the next operation of a shared connection,
        /// an engine of a new process) did not sync the kept WAL itself, and its data proof failed on
        /// the header the backfill rewrote: it emptied the WAL, and the commit acknowledged durable
        /// was lost to the WAL written back ahead of the backfill. Every checkpoint now syncs the data
        /// file before it writes, so every engine keeps the WAL and writes no page until a data sync
        /// succeeds, whoever synced it: the data file as last synced with the WAL as written back
        /// holds every row and every document of the new collection, whole.
        /// </summary>
        [Theory]
        [InlineData("reopen")]
        [InlineData("restart")] // the second engine is of a new process
        [InlineData("shared")]
        public void Wal_kept_by_one_engine_is_kept_by_the_next(string mode)
        {
            using var file = new TempFile();
            Setup(file.Filename);
            var logName = FileHelper.GetLogFile(file.Filename);
            using var power = new SyncPowerLossModel(file.Filename);
            if (mode != "shared")
            {
                using (var db = new LiteDatabase(new LiteEngine(power.Settings())))
                {
                    db.CheckpointSize = 0;
                    CommitWithNewPages(db);
                    DurableLogFlush(db).Should().BeTrue();
                    power.DataFails = true;
                    db.Checkpoint();
                }
                new FileInfo(logName).Length.Should().BeGreaterThan(0, "the first engine kept its synced WAL");
                if (mode == "restart") DurableHeaders.Forget(file.Filename);
                using (var db = new LiteDatabase(new LiteEngine(power.Settings()))) db.Checkpoint();
            }
            else
            {
                using var shared = new SharedEngine(power.Settings());
                using var db = new LiteDatabase(shared, disposeOnClose: false);
                power.DataFails = true;
                CommitWithNewPages(db); // its log sync still runs: its data proof matches the synced header
                DurableLogFlush(db).Should().BeTrue();
                for (var i = 0; i < 3; i++) db.Checkpoint();
            }
            SyncPowerLossModel.ReadShared(file.Filename).Should().Equal(power.Capture().Data, "no engine wrote a page");

            var image = (power.Capture().Data, SyncPowerLossModel.ReadShared(logName));
            FilePowerLossModel.Open(image, db => AssertState(db, 1, extra: true));
        }

        /// <summary>
        /// While the WAL is kept, an automatic checkpoint first retries the data sync and does
        /// nothing while it fails, instead of rescanning the growing WAL at every commit; the first
        /// one after the data file syncs again empties it. $database.walKept reports the kept WAL.
        /// </summary>
        [Fact]
        public void Automatic_checkpoints_wait_for_the_data_file_while_the_wal_is_kept()
        {
            using var file = new TempFile();
            Setup(file.Filename);
            var logName = FileHelper.GetLogFile(file.Filename);
            using var power = new SyncPowerLossModel(file.Filename);
            var checkpoints = 0;
            var settings = power.Settings();
            settings.CheckpointStage = stage => { if (stage == "before-commit-lock") checkpoints++; };
            using var db = new LiteDatabase(new LiteEngine(settings));
            db.CheckpointSize = 10;
            Update(db, 1);
            DurableLogFlush(db).Should().BeTrue();

            power.DataFails = true;
            for (var id = 1; id <= 200; id++) db.GetCollection("log").Insert(new BsonDocument { ["_id"] = id, ["text"] = new string('t', 3000) });
            checkpoints.Should().BeLessThan(5, "a kept WAL is not rescanned at every commit");
            db.GetCollection("$database").FindAll().Single()["walKept"].AsBoolean.Should().BeTrue();
            new FileInfo(logName).Length.Should().BeGreaterThan(80L * Constants.PAGE_SIZE, "the WAL with the durable commit is kept");
            power.AfterPowerLoss(Rows).Should().Be(1);

            power.DataFails = false;
            db.GetCollection("log").Insert(new BsonDocument { ["_id"] = 201 });
            new FileInfo(logName).Length.Should().BeLessThan(20L * Constants.PAGE_SIZE, "the next automatic checkpoint empties it");
            db.GetCollection("log").Count().Should().Be(201);
            db.GetCollection("$database").FindAll().Single()["walKept"].AsBoolean.Should().BeFalse();
        }

        /// <summary>
        /// An encrypted stream synced its file when created, also one a pool creates for a concurrent
        /// read: outside the engine's barriers, it could make an emptied WAL durable ahead of its
        /// backfill. Only a writer syncs its preamble now; concurrent readers sync nothing.
        /// </summary>
        [Fact]
        public void Encrypted_readers_do_not_sync_the_files()
        {
            using var file = new TempFile();
            using var power = new SyncPowerLossModel(file.Filename);
            var settings = power.Settings();
            settings.Password = "secret";
            using var engine = new LiteEngine(settings);
            using var db = new LiteDatabase(engine, disposeOnClose: false);
            db.CheckpointSize = 0;
            db.GetCollection("rows").Insert(Enumerable.Range(1, Rows).Select(id => MvccRetirementScenario.Document(id, 1)));
            var (dataSyncs, logSyncs) = (power.DataSyncs, power.LogSyncs);

            using var barrier = new Barrier(6);
            var readers = Enumerable.Range(0, 6).Select(_ => new Thread(() =>
            {
                using var reader = engine.Query("rows", new Query());
                reader.Read().Should().BeTrue();
                barrier.SignalAndWait(TimeSpan.FromSeconds(20)).Should().BeTrue();
            })).ToArray();
            foreach (var reader in readers) reader.Start();
            foreach (var reader in readers) reader.Join(TimeSpan.FromSeconds(30)).Should().BeTrue();

            (power.DataSyncs, power.LogSyncs).Should().Be((dataSyncs, logSyncs), "six concurrent readers open streams of their own");
        }

        /// <summary>
        /// A rebuild installs a new data file at the database's path. The process remembers the
        /// header the old file synced there (DurableHeaders); the installed file has another one, so
        /// an engine proves it before its first log sync instead of taking it as synced.
        /// </summary>
        [Fact]
        public void File_a_rebuild_installs_is_proven_again()
        {
            using var file = new TempFile();
            Setup(file.Filename);
            using (var db = new LiteDatabase(file.Filename)) db.Rebuild();

            using var power = new FilePowerLossModel(file.Filename);
            using var reopened = new LiteDatabase(file.Filename);
            var atOpen = power.DataSyncs;
            Update(reopened, 1);
            DurableLogFlush(reopened).Should().BeTrue();
            (power.DataSyncs - atOpen).Should().Be(1, "the installed file's header is not the one the old file synced at this path");
        }

        private const int Rows = 64;

        private static void Setup(string filename)
        {
            using var setup = new LiteDatabase(filename);
            setup.GetCollection("rows").Insert(Enumerable.Range(1, Rows).Select(id => MvccRetirementScenario.Document(id, 0)));
            setup.GetCollection("rows").EnsureIndex("value");
        }

        /// <summary>
        /// Every row holds <paramref name="value"/>, whole, and the value index finds each; the
        /// collection <see cref="CommitWithNewPages"/> adds holds each of its documents, whole, or
        /// does not exist.
        /// </summary>
        private static int AssertState(LiteDatabase db, int value, bool extra)
        {
            SyncPowerLossModel.AssertRows(db, Rows, value);
            if (!extra) db.CollectionExists("extra").Should().BeFalse();
            else db.GetCollection("extra").FindAll().OrderBy(x => x["_id"].AsInt32).Should().BeEquivalentTo(
                Enumerable.Range(1, 40).Select(Extra), o => o.WithStrictOrdering());
            return value;
        }

        private static BsonDocument Extra(int id) => new BsonDocument { ["_id"] = id, ["p"] = new string('e', 3000) };

        private static void Update(LiteDatabase db, int value) =>
            db.GetCollection("rows").Upsert(Enumerable.Range(1, Rows).Select(id => MvccRetirementScenario.Document(id, value)));

        /// <summary>Updates every row and inserts pages of a new collection, so the backfill rewrites the header.</summary>
        private static void CommitWithNewPages(LiteDatabase db)
        {
            db.BeginTrans();
            db.GetCollection("rows").Upsert(Enumerable.Range(1, Rows).Select(id => MvccRetirementScenario.Document(id, 1)));
            db.GetCollection("extra").Insert(Enumerable.Range(1, 40).Select(Extra));
            db.Commit().Should().BeTrue();
        }

        private static bool DurableLogFlush(LiteDatabase db) =>
            db.GetCollection("$database").FindAll().Single()["durableLogFlush"].AsBoolean;

    }
}
#endif
