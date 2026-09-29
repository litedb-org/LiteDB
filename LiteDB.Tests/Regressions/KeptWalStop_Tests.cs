#if DEBUG || TESTING
using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using FluentAssertions;
using LiteDB.Engine;
using LiteDB.Tests.Issues;
using Xunit;
using static LiteDB.Constants;

namespace LiteDB.Tests.Regressions
{
    /// <summary>
    /// The WAL and its header journal (the log's recovery copy of the data header) are removed only
    /// after a data sync that covers what they protect succeeded (#2242). A checkpoint writes only
    /// to a data file that just synced; one whose data file stops syncing after it wrote throws
    /// "stopped syncing" and stops the engine with both kept, after recording the failure
    /// (decision 6 of docs/decisions/durability-policy.md); an open whose repaired header cannot
    /// sync keeps the journal; a conversion whose header cannot sync keeps its journal. Power-loss
    /// images keep each file as of its last successful sync, or take the WAL as written back and
    /// tear the data header.
    /// </summary>
    [Trait("Category", "IoSafety")]
    [Collection(NativeFileSyncCollection.Name)]
    public partial class KeptWalStop_Tests
    {
        /// <summary>
        /// The data file stops syncing at a full checkpoint's salt rotation ("before-reclaim"), after
        /// its backfill synced. The checkpoint then emptied the WAL with its header journal while the
        /// rotated header was in the OS cache only. It now throws "stopped syncing" and stops the
        /// engine with both kept; the stopped engine refuses reads and writes without changing a file. The data file as last synced or with its header torn,
        /// with the WAL as written back or as last synced, opens with every collection and document,
        /// the drop acknowledged durable included. The rotation changes only the header's first sector, so a
        /// 512- or 4096-byte tear holds all of it; a 64-byte tear leaves a header whose checksum
        /// fails, which only the kept journal repairs.
        /// </summary>
        [Theory]
        [InlineData(0)]  // the data file as last synced
        [InlineData(64)] // torn inside the rotation's sector
        [InlineData(512)]
        [InlineData(4096)]
        public void Data_sync_lost_at_the_salt_rotation_keeps_the_wal_and_its_header_journal(int tear)
        {
            using var file = new TempFile();
            SetupCollections(file.Filename);
            var (synced, data, log) = StopCheckpoint(file.Filename, "before-reclaim");
            log.Length.Should().BeGreaterThan(0, "the WAL is kept: before the fix this checkpoint emptied it");
            HasJournal(log).Should().BeTrue("the header journal is kept");
            data.Skip(PAGE_SIZE).Should().Equal(synced.Data.Skip(PAGE_SIZE), "only the rotated header was written after the backfill synced");
            data.Take(PAGE_SIZE).Should().NotEqual(synced.Data.Take(PAGE_SIZE));

            var torn = Tear(synced.Data, data, tear);
            if (tear == 64) AssertOnlyTheJournalRepairs(torn, data);
            FilePowerLossModel.Open((torn, log), AssertCollections);
            FilePowerLossModel.Open((torn, synced.Log), AssertCollections);
        }

        /// <summary>
        /// The data file stops syncing after a full checkpoint's own data sync, before its backfill's
        /// ("data-page"): the checkpoint went on with its WAL kept but retired its header journal,
        /// so a torn header (the drop rewrote more than the first sector) could not be repaired. It
        /// now stops before it removes anything: the torn header with the WAL as written back opens
        /// with every collection and document.
        /// </summary>
        [Theory]
        [InlineData(512)]
        [InlineData(4096)]
        public void Data_sync_lost_at_the_backfill_keeps_the_header_journal(int tear)
        {
            using var file = new TempFile();
            SetupCollections(file.Filename);
            var (synced, data, log) = StopCheckpoint(file.Filename, "data-page");
            HasJournal(log).Should().BeTrue("the header journal is kept");
            var changed = Enumerable.Range(0, PAGE_SIZE).Where(i => synced.Data[i] != data[i]).ToArray();
            changed.Max().Should().BeGreaterThan(tear, "the header change spans the tear");

            var torn = Tear(synced.Data, data, tear);
            AssertOnlyTheJournalRepairs(torn, data);
            FilePowerLossModel.Open((torn, log), AssertCollections);
            FilePowerLossModel.Open((synced.Data, log), AssertCollections);
            FilePowerLossModel.Open((data, log), AssertCollections);
        }

        /// <summary>
        /// A fresh engine over a WAL an earlier engine kept (a new process, or every operation of a
        /// shared connection) checkpointed it against a data file that cannot sync: it wrote the
        /// backfill and a header journal to a file that could not make them durable. Its checkpoint
        /// now syncs the data file first and writes nothing, the WAL stays as it is, and walKept
        /// reports it, also for a shared connection's later operations.
        /// </summary>
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void Fresh_engine_over_a_kept_wal_writes_nothing_while_the_data_file_cannot_sync(bool shared)
        {
            using var file = new TempFile();
            SetupRows(file.Filename);
            var logName = FileHelper.GetLogFile(file.Filename);
            using var power = new SyncPowerLossModel(file.Filename);
            using (var db = new LiteDatabase(new LiteEngine(power.Settings())))
            {
                db.CheckpointSize = 0;
                UpdateRows(db, 1);
                Info(db)["durableLogFlush"].AsBoolean.Should().BeTrue();
                power.DataFails = true;
                db.Checkpoint();
                Info(db)["walKept"].AsBoolean.Should().BeTrue();
            }
            var kept = SyncPowerLossModel.ReadShared(logName);
            kept.Length.Should().BeGreaterThan(0);
            var synced = power.Capture().Data;

            var pages = 0;
            var settings = power.Settings();
            settings.CheckpointStage = stage => { if (stage == "data-page") pages++; };
            using (var engine = shared ? (ILiteEngine)new SharedEngine(settings) : new LiteEngine(settings))
            using (var db = new LiteDatabase(engine, disposeOnClose: false))
            {
                for (var i = 0; i < 3; i++)
                {
                    SyncPowerLossModel.AssertRows(db, Rows, 1);
                    db.Checkpoint();
                }
                Info(db)["walKept"].AsBoolean.Should().BeTrue("the WAL is kept until the data file syncs");
            }
            pages.Should().Be(0, "no checkpoint writes a page to a data file that cannot sync");
            SyncPowerLossModel.ReadShared(logName).Should().Equal(kept, "the WAL is kept as it is");
            SyncPowerLossModel.ReadShared(file.Filename).Should().Equal(synced);
            power.AssertAfterPowerLoss(Rows, 1);
        }

        /// <summary>
        /// A restart over a kept WAL reported walKept=false before it tried a data sync: its report now
        /// tries one first. Its first checkpoint cannot know that the data file cannot sync (another
        /// process found it), so it scans the WAL and stops at its pre-write sync; later checkpoints of
        /// the engine retry the data sync before they take a lock or scan, and stop there.
        /// </summary>
        [Fact]
        public void Restart_over_a_kept_wal_reports_it_and_stops_scanning_once_it_knows()
        {
            using var file = new TempFile();
            SetupRows(file.Filename);
            using var power = new SyncPowerLossModel(file.Filename);
            using (var db = new LiteDatabase(new LiteEngine(power.Settings())))
            {
                db.CheckpointSize = 0;
                UpdateRows(db, 1);
                power.DataFails = true;
                db.Checkpoint();
            }

            var scans = 0;
            var settings = power.Settings();
            settings.CheckpointStage = stage => { if (stage == "before-commit-lock") scans++; };
            using (var db = new LiteDatabase(new LiteEngine(settings)))
            {
                db.Checkpoint();
                scans.Should().Be(1, "the first checkpoint found out at its pre-write sync");
                db.Checkpoint();
                db.Checkpoint();
                scans.Should().Be(1, "later checkpoints stopped at their data sync");
                SyncPowerLossModel.AssertRows(db, Rows, 1);
            }
            using (var db = new LiteDatabase(new LiteEngine(settings)))
            {
                var syncs = power.DataSyncs;
                Info(db)["walKept"].AsBoolean.Should().BeTrue("the report tried the data sync");
                power.DataSyncs.Should().Be(syncs + 1);
            }

            power.DataFails = false;
            using (var db = new LiteDatabase(new LiteEngine(settings)))
            {
                Info(db)["walKept"].AsBoolean.Should().BeFalse("the data file syncs again");
                db.Checkpoint();
                Info(db)["logFileSize"].AsInt64.Should().Be(0);
                SyncPowerLossModel.AssertRows(db, Rows, 1);
            }
            power.AssertAfterPowerLoss(Rows, 1);
        }

        /// <summary>
        /// Only the legacy conversion resets the WAL, once drained: a reset of a WAL that holds frames
        /// would empty it without a data sync, so it throws before changing anything.
        /// </summary>
        [Fact]
        public void Wal_reset_refuses_a_wal_that_holds_frames()
        {
            using var file = new TempFile();
            SetupRows(file.Filename);
            using var engine = new LiteEngine(new EngineSettings { Filename = file.Filename });
            using (var db = new LiteDatabase(engine, disposeOnClose: false))
            {
                db.CheckpointSize = 0;
                UpdateRows(db, 1);
            }
            var logName = FileHelper.GetLogFile(file.Filename);
            var log = SyncPowerLossModel.ReadShared(logName);
            log.Length.Should().BeGreaterThan(0);
            Action reset = () => engine.GetWalIndex().Clear();
            reset.Should().Throw<InvalidOperationException>().WithMessage("Only a drained legacy WAL can be reset.");
            SyncPowerLossModel.ReadShared(logName).Should().Equal(log);
            using var again = new LiteDatabase(engine, disposeOnClose: false);
            SyncPowerLossModel.AssertRows(again, Rows, 1);
        }

        /// <summary>
        /// A shared connection reports its kept WAL connection-wide, since each operation's engine
        /// is fresh, and stops once a data sync succeeds: here a partial checkpoint (a live reader
        /// keeps the WAL) syncs the data file, and the WAL left is no longer a kept one.
        /// </summary>
        [Fact]
        public void Shared_connection_reports_a_kept_wal_until_a_data_sync_succeeds()
        {
            using var file = new TempFile();
            SetupRows(file.Filename);
            using var power = new SyncPowerLossModel(file.Filename);
            using var engine = new SharedEngine(power.Settings());
            using var db = new LiteDatabase(engine, disposeOnClose: false);
            db.CheckpointSize = 0;
            power.DataFails = true;
            UpdateRows(db, 1);
            db.Checkpoint();
            Info(db)["walKept"].AsBoolean.Should().BeTrue("the next operation's engine has not synced the data file itself");

            power.DataFails = false;
            using (var reader = engine.Query("rows", new Query()))
            {
                reader.Read().Should().BeTrue();
                var syncs = power.DataSyncs;
                var worker = new System.Threading.Thread(() => db.Checkpoint());
                worker.Start();
                worker.Join();
                power.DataSyncs.Should().BeGreaterThan(syncs, "the partial checkpoint synced the data file");
                Info(db)["logFileSize"].AsInt64.Should().BeGreaterThan(0, "the live reader keeps the WAL");
                Info(db)["walKept"].AsBoolean.Should().BeFalse("the data file synced");
            }
            SyncPowerLossModel.AssertRows(db, Rows, 1);
        }

        private const int Collections = 150;
        private const int Rows = 64;

        private static string Name(int i) => $"collection_with_a_long_name_{i:D3}";

        private static BsonDocument Document(int i) => new BsonDocument { ["_id"] = 1, ["n"] = i };

        /// <summary>Collections whose names fill most of the header, so a drop rewrites it past its first sectors.</summary>
        private static void SetupCollections(string filename)
        {
            using var setup = new LiteDatabase(filename);
            for (var i = 0; i < Collections; i++) setup.GetCollection(Name(i)).Insert(Document(i));
        }

        /// <summary>
        /// Drop the first collection (acknowledged durable), then checkpoint with the data file
        /// failing from <paramref name="stage"/> on: the checkpoint throws "stopped syncing" and the
        /// engine stops. The failure is recorded (decision 6) before the stop; every later read and
        /// write throws the stop error, and neither changes a file. Returns the files as last synced,
        /// and the data file and WAL as they are.
        /// </summary>
        private static ((byte[] Data, byte[] Log) Synced, byte[] Data, byte[] Log) StopCheckpoint(string filename, string stage)
        {
            using var power = new SyncPowerLossModel(filename);
            var engine = new LiteEngine(power.Settings());
            using var db = new LiteDatabase(engine);
            db.CheckpointSize = 0;
            db.DropCollection(Name(0)).Should().BeTrue();
            Info(db)["durableLogFlush"].AsBoolean.Should().BeTrue("the drop is acknowledged durable");
            power.RetirementStage = stage;
            Action checkpoint = () => db.Checkpoint();
            var thrown = checkpoint.Should().Throw<IOException>().WithMessage("The data file stopped syncing*").Which;
            power.DataFails.Should().BeTrue("the checkpoint reached " + stage);
            var synced = power.Capture();
            var (data, log) = (SyncPowerLossModel.ReadShared(filename), SyncPowerLossModel.ReadShared(FileHelper.GetLogFile(filename)));

            TerminalStopAfterWriteFailure.AssertRecorded(engine, "A checkpoint", "data", "The data file stopped syncing to the device during a checkpoint");
            TerminalStopAfterWriteFailure.AssertRefused(() => AssertCollections(db), thrown);
            TerminalStopAfterWriteFailure.AssertRefused(() => db.GetCollection(Name(1)).Insert(new BsonDocument { ["_id"] = 2 }), thrown);
            TerminalStopAfterWriteFailure.AssertRefused(() => db.DropCollection(Name(1)), thrown);
            SyncPowerLossModel.ReadShared(filename).Should().Equal(data, "the stopped engine writes nothing");
            SyncPowerLossModel.ReadShared(FileHelper.GetLogFile(filename)).Should().Equal(log);
            power.Capture().Data.Should().Equal(synced.Data);
            power.Capture().Log.Should().Equal(synced.Log);
            return (synced, data, log);
        }

        /// <summary>The data file as last synced with the first <paramref name="bytes"/> of its header written back.</summary>
        private static byte[] Tear(byte[] synced, byte[] current, int bytes)
        {
            var torn = (byte[])synced.Clone();
            Buffer.BlockCopy(current, 0, torn, 0, bytes);
            return torn;
        }

        /// <summary>The torn header is neither the old nor the new one: without the journal it does not open.</summary>
        private static void AssertOnlyTheJournalRepairs(byte[] torn, byte[] current)
        {
            torn.Take(PAGE_SIZE).Should().NotEqual(current.Take(PAGE_SIZE), "the tear splits the header change");
            Action unrepaired = () => FilePowerLossModel.Open((torn, new byte[0]), AssertCollections);
            unrepaired.Should().Throw<LiteException>().WithMessage("Checksum mismatch in Data file at position 0*");
        }

        private static bool HasJournal(byte[] log)
        {
            using var stream = new MemoryStream(log);
            return HeaderJournal.Read(stream) != null;
        }

        /// <summary>Every collection but the dropped one, each with its one document, whole.</summary>
        private static int AssertCollections(LiteDatabase db)
        {
            db.GetCollectionNames().OrderBy(x => x, StringComparer.Ordinal).Should().Equal(Enumerable.Range(1, Collections - 1).Select(Name));
            for (var i = 1; i < Collections; i++)
                db.GetCollection(Name(i)).FindAll().Should().BeEquivalentTo(new[] { Document(i) }, o => o.WithStrictOrdering());
            return Collections - 1;
        }

        private static void SetupRows(string filename)
        {
            using var setup = new LiteDatabase(filename);
            setup.GetCollection("rows").Insert(Enumerable.Range(1, Rows).Select(id => LiteDB.Internals.MvccRetirementScenario.Document(id, 0)));
            setup.GetCollection("rows").EnsureIndex("value");
        }

        private static void UpdateRows(LiteDatabase db, int value) =>
            db.GetCollection("rows").Upsert(Enumerable.Range(1, Rows).Select(id => LiteDB.Internals.MvccRetirementScenario.Document(id, value)));

        private static BsonDocument Info(LiteDatabase db) => db.GetCollection("$database").FindAll().Single();

    }
}
#endif
