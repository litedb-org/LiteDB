#if DEBUG || TESTING
using System;
using System.Collections.Generic;
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
    /// "stopped syncing" and stops the engine with both kept; an open whose repaired header cannot
    /// sync keeps the journal; a conversion whose header cannot sync keeps its journal. Power-loss
    /// images keep each file as of its last successful sync, or take the WAL as written back and
    /// tear the data header.
    /// </summary>
    [Trait("Category", "IoSafety")]
    [Collection(NativeFileSyncCollection.Name)]
    public class KeptWalStop_Tests
    {
        /// <summary>
        /// The data file stops syncing at a full checkpoint's salt rotation ("before-reclaim"), after
        /// its backfill synced. The checkpoint then emptied the WAL with its header journal while the
        /// rotated header was in the OS cache only. It now throws "stopped syncing" and stops the
        /// engine with both kept. The data file as last synced or with its header torn, with the WAL
        /// as written back or as last synced, opens with every collection and document, the drop
        /// acknowledged durable included. The rotation changes only the header's first sector, so a
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
        /// Open-time recovery of a torn header on a data file that cannot sync: it rewrote the
        /// header in the OS cache and retired the journal although that header never synced. The
        /// open is now refused and the journal kept: another power loss still recovers, a read-only
        /// open reads everything, and once the data file syncs the open recovers.
        /// </summary>
        [Fact]
        public void Open_recovery_keeps_the_header_journal_while_the_data_file_cannot_sync()
        {
            using var file = new TempFile();
            SetupCollections(file.Filename);
            var (synced, data, log) = StopCheckpoint(file.Filename, "data-page");
            var torn = Tear(synced.Data, data, 512);

            using var image = new TempFile();
            var imageLog = FileHelper.GetLogFile(image.Filename);
            File.WriteAllBytes(image.Filename, torn);
            File.WriteAllBytes(imageLog, log);
            try
            {
                using var power = new FilePowerLossModel(image.Filename) { DataFails = true };
                Action open = () => new LiteDatabase(image.Filename).Dispose();
                open.Should().Throw<IOException>().WithMessage("Cannot recover this database now*");
                SyncPowerLossModel.ReadShared(imageLog).Should().Equal(log, "the journal stays until the repaired header synced");
                power.Capture().Data.Should().Equal(torn, "the repaired header did not sync");
                FilePowerLossModel.Open(power.Capture(), AssertCollections); // a second power loss
                FilePowerLossModel.Open((power.Capture().Data, SyncPowerLossModel.ReadShared(imageLog)), AssertCollections);
                using (var readOnly = new LiteDatabase($"Filename={image.Filename};ReadOnly=true")) AssertCollections(readOnly);

                power.DataFails = false;
                using (var db = new LiteDatabase(image.Filename)) AssertCollections(db);
                FilePowerLossModel.Open(power.Capture(), AssertCollections);
            }
            finally { File.Delete(imageLog); }
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
            DurableHeaders.Forget(file.Filename); // a new process

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
        /// A restart over a kept WAL: before it tried a data sync, the engine reported walKept=false,
        /// and its first checkpoint scanned the whole WAL before its data sync failed. Its report and
        /// its checkpoint now try the data sync first; the checkpoint stops there.
        /// </summary>
        [Fact]
        public void Restart_over_a_kept_wal_reports_it_and_checkpoints_without_scanning()
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
            DurableHeaders.Forget(file.Filename); // a new process

            var scans = 0;
            var settings = power.Settings();
            settings.CheckpointStage = stage => { if (stage == "before-commit-lock") scans++; };
            using (var db = new LiteDatabase(new LiteEngine(settings)))
            {
                db.Checkpoint();
                scans.Should().Be(0, "the checkpoint stopped at its data sync");
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

        /// <summary>
        /// A 5.0.21 file whose data file syncs for the conversion's first two syncs, not for the sync
        /// of its converted header: the conversion emptied the log (the legacy header's backup and
        /// its journal) anyway. It is now refused with both kept; the power-loss image and, once the
        /// data file syncs, the database itself open converted with every document.
        /// </summary>
        [Fact]
        public void Conversion_whose_header_sync_fails_is_refused_until_the_data_file_syncs()
        {
            using var file = new TempFile();
            var original = Fixture("plain.db");
            File.WriteAllBytes(file.Filename, original);
            var logName = FileHelper.GetLogFile(file.Filename);
            var expected = LegacyContents(file.Filename);
            try
            {
                (byte[] Data, byte[] Log) image;
                using (var power = new FilePowerLossModel(file.Filename) { DataFailsFromSync = 3 })
                {
                    Action open = () => new LiteDatabase(file.Filename).Dispose();
                    open.Should().Throw<IOException>().WithMessage("Cannot convert this legacy database now*cannot sync*");
                    power.DataSyncs.Should().BeGreaterOrEqualTo(3, "the header's sync was reached");
                    SyncPowerLossModel.ReadShared(logName).Length.Should().BeGreaterThan(0, "the legacy header backup and the journal are kept");
                    image = power.Capture();
                }
                image.Data.Should().Equal(original, "the converted header never synced");
                FilePowerLossModel.Open(image, db => AssertConverted(db, expected));
                using var converted = new LiteDatabase(file.Filename);
                AssertConverted(converted, expected);
            }
            finally { File.Delete(logName); }
        }

        /// <summary>
        /// A WAL in memory (LiteDatabase(Stream) over a caller FileStream) survives no power loss,
        /// so nothing waits for a data sync on its behalf: a 5.0.21 file converts on a data file
        /// that cannot sync, and the WAL is still emptied by checkpoints.
        /// </summary>
        [Fact]
        public void Legacy_file_stream_with_an_in_memory_wal_converts_on_a_data_file_that_cannot_sync()
        {
            using var file = new TempFile();
            File.WriteAllBytes(file.Filename, Fixture("plain.db"));
            var expected = LegacyContents(file.Filename);
            var rejected = 0;
            NativeFileSync.SimulateErrno = _ => { rejected++; return 22; };
            try
            {
                using var stream = new FileStream(file.Filename, FileMode.Open, FileAccess.ReadWrite);
                using var db = new LiteDatabase(stream);
                AssertConverted(db, expected);
                rejected.Should().BeGreaterThan(0, "the data file answered \"cannot sync\"");
                db.CheckpointSize = 10;
                for (var id = 1; id <= 200; id++) db.GetCollection("log").Insert(new BsonDocument { ["_id"] = id, ["text"] = new string('t', 3000) });
                Info(db)["walKept"].AsBoolean.Should().BeFalse();
                Info(db)["logFileSize"].AsInt64.Should().BeLessThan(40 * PAGE_SIZE, "the in-memory WAL is still emptied");
                db.GetCollection("log").Count().Should().Be(200);
            }
            finally { NativeFileSync.SimulateErrno = null; }
        }

        [Fact]
        public void Database_file_sizes_are_int64()
        {
            using var db = new LiteDatabase(":memory:");
            db.GetCollection("rows").Insert(new BsonDocument { ["_id"] = 1 });
            var info = Info(db);
            info["logFileSize"].Type.Should().Be(BsonType.Int64);
            info["dataFileSize"].Type.Should().Be(BsonType.Int64);
            info["logFileSize"].AsInt64.Should().BeGreaterThan(0);
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
        /// engine stops. Returns the files as last synced, and the data file and WAL as they are.
        /// </summary>
        private static ((byte[] Data, byte[] Log) Synced, byte[] Data, byte[] Log) StopCheckpoint(string filename, string stage)
        {
            using var power = new SyncPowerLossModel(filename);
            using var db = new LiteDatabase(new LiteEngine(power.Settings()));
            db.CheckpointSize = 0;
            db.DropCollection(Name(0)).Should().BeTrue();
            Info(db)["durableLogFlush"].AsBoolean.Should().BeTrue("the drop is acknowledged durable");
            power.RetirementStage = stage;
            Action checkpoint = () => db.Checkpoint();
            checkpoint.Should().Throw<IOException>().WithMessage("The data file stopped syncing*");
            power.DataFails.Should().BeTrue("the checkpoint reached " + stage);
            Action query = () => db.GetCollectionNames().ToArray();
            query.Should().Throw<IOException>().WithMessage("Engine closed after an I/O failure*", "the failed checkpoint stopped the engine");
            return (power.Capture(), SyncPowerLossModel.ReadShared(filename), SyncPowerLossModel.ReadShared(FileHelper.GetLogFile(filename)));
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

        private static Dictionary<string, string[]> LegacyContents(string filename)
        {
            using var legacy = new LiteDatabase($"Filename={filename};ReadOnly=true;Legacy Index Scan=true");
            var contents = Contents(legacy);
            contents.Values.Sum(x => x.Length).Should().BeGreaterThan(0);
            return contents;
        }

        private static Dictionary<string, string[]> Contents(LiteDatabase db) => db.GetCollectionNames().ToDictionary(name => name,
            name => db.GetCollection(name).FindAll().OrderBy(x => x["_id"]).Select(x => JsonSerializer.Serialize(x)).ToArray());

        /// <summary>The database is converted and holds exactly <paramref name="expected"/>.</summary>
        private static int AssertConverted(LiteDatabase db, Dictionary<string, string[]> expected)
        {
            Info(db)["checksums"].AsBoolean.Should().BeTrue();
            Contents(db).Should().BeEquivalentTo(expected, o => o.WithStrictOrdering());
            return expected.Count;
        }

        private static byte[] Fixture(string name)
        {
            using var resource = typeof(KeptWalStop_Tests).Assembly.GetManifestResourceStream("LiteDB.Tests.Resources.IndexMigration_5_0_21.zip");
            using var zip = new ZipArchive(resource, ZipArchiveMode.Read);
            using var entry = zip.GetEntry(name).Open();
            using var bytes = new MemoryStream();
            entry.CopyTo(bytes);
            return bytes.ToArray();
        }
    }
}
#endif
