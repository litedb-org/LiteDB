#if DEBUG || TESTING
using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using FluentAssertions;
using LiteDB.Engine;
using LiteDB.Internals;
using LiteDB.Tests.Issues;
using Xunit;

namespace LiteDB.Tests.Regressions
{
    /// <summary>
    /// A fresh engine (another connection, the next LiteDatabase, every shared-mode operation)
    /// knows nothing of a data file that answered "cannot sync" (#2242) for an earlier engine.
    /// When that engine left a changed data header in the OS cache only (a salt rotation, a format
    /// conversion), the fresh engine's commits depend on it, yet it reported them durable after
    /// syncing only the WAL, and a power loss (each file as of its last successful sync) lost them.
    /// Before its first commit an engine whose data is a file (opened by the engine or passed as a
    /// FileStream) now proves that the data header is on the device: it syncs the data file, or skips
    /// that while the header is one a successful sync in this process left. Commits then stay durable
    /// in the WAL while only the data file cannot sync (decision 4 of
    /// docs/decisions/durability-policy.md); where the header cannot be proven so (a database created
    /// there, a new process), it is anchored in the WAL first (decisions 8 and 10).
    /// </summary>
    [Trait("Category", "RegressionSince5021")]
    [Collection(NativeFileSyncCollection.Name)]
    public class FreshEngineDurability_Tests
    {
        /// <summary>
        /// The data file stops syncing at a full checkpoint's salt rotation, after its backfill
        /// synced. The checkpoint emptied the WAL anyway, and a fresh engine then reported commits
        /// durable that depended on the rotated header in the OS cache only. The checkpoint now stops
        /// ("stopped syncing", the engine closes) before it removes the WAL or its header journal, so
        /// the data file as last synced with the WAL, as last synced or as written back, holds every
        /// commit. A fresh engine over the kept journal cannot recover while the data file still
        /// cannot sync: it opens read-only ($database.readOnlyReason "Cannot recover this database
        /// now") and reads every commit, its write throws before any change, and it writes nothing,
        /// leaving the journal; once the data file syncs, it recovers every commit, and a commit it
        /// reports durable survives the power loss.
        /// </summary>
        [Theory]
        [InlineData(false)]
        [InlineData(true)] // the data file syncs again for the fresh engine
        public void Fresh_engine_after_a_salt_rotation_whose_data_sync_failed(bool dataSyncsAgain)
        {
            using var file = new TempFile();
            Setup(file.Filename, index: true);
            var logName = FileHelper.GetLogFile(file.Filename);
            using var power = new FilePowerLossModel(file.Filename);
            try
            {
                var settings = new EngineSettings { Filename = file.Filename };
                settings.CheckpointStage = stage => { if (stage == "before-reclaim") power.DataFails = true; };
                using (var first = new LiteDatabase(new LiteEngine(settings)))
                {
                    first.CheckpointSize = 0;
                    for (var value = 1; value <= 5; value++)
                    {
                        Update(first, value);
                        DurableLogFlush(first).Should().BeTrue();
                    }
                    Action checkpoint = () => first.Checkpoint(); // the backfill syncs, the new salt's header does not
                    checkpoint.Should().Throw<IOException>().WithMessage("The data file stopped syncing*");
                    power.DataFails.Should().BeTrue("the checkpoint reached its salt rotation");
                }
                var kept = SyncPowerLossModel.ReadShared(logName);
                kept.Length.Should().BeGreaterThan(0, "the WAL and its header journal are kept");
                power.AfterPowerLoss(db => AssertRows(db, 5));
                FilePowerLossModel.Open((power.Capture().Data, kept), db => AssertRows(db, 5));

                power.DataFails = !dataSyncsAgain;
                if (!dataSyncsAgain)
                {
                    var data = SyncPowerLossModel.ReadShared(file.Filename);
                    using (var readOnly = new LiteDatabase(file.Filename))
                        UnsyncedReadOnlyOpen_Tests.AssertReadOnlyFallback(readOnly, UnsyncedReadOnlyOpen_Tests.RecoveryRefused, db => AssertRows(db, 5));
                    SyncPowerLossModel.ReadShared(file.Filename).Should().Equal(data, "the read-only open wrote nothing");
                    SyncPowerLossModel.ReadShared(logName).Should().Equal(kept, "the header journal stays until the repaired header synced");
                    power.AfterPowerLoss(db => AssertRows(db, 5));
                    power.DataFails = false; // the storage syncs again
                }
                using (var second = new LiteDatabase(file.Filename))
                {
                    AssertRows(second, 5);
                    Update(second, 6);
                    DurableLogFlush(second).Should().BeTrue("a fresh engine proves the data file before its first durable commit");
                }
                power.AfterPowerLoss(db => AssertRows(db, 6));
            }
            finally { File.Delete(logName); }
        }

        /// <summary>
        /// A 5.0.21 file (IndexMigration_5_0_21.zip, its WAL empty) on storage whose data file cannot
        /// sync while its WAL can. The converted header reached the OS cache only while the
        /// checksummed frames written after it became durable, so a power loss left them beside the
        /// legacy header. The conversion empties the log only after a data sync that succeeds, so it
        /// is not run: the open falls back to read-only ("Cannot convert this legacy database now"),
        /// reads every row, refuses writes, and leaves the legacy file unchanged; once the data file
        /// syncs, it converts and commits are durable.
        /// </summary>
        [Fact]
        public void Conversion_opens_read_only_where_only_the_data_file_cannot_sync()
        {
            using var file = new TempFile();
            var original = Fixture("plain.db");
            File.WriteAllBytes(file.Filename, original);
            var logName = FileHelper.GetLogFile(file.Filename);
            using var power = new FilePowerLossModel(file.Filename) { DataFails = true };
            try
            {
                using (var legacy = new LiteDatabase(file.Filename))
                    UnsyncedReadOnlyOpen_Tests.AssertReadOnlyFallback(legacy, UnsyncedReadOnlyOpen_Tests.ConversionRefused,
                        db => UnsyncedReadOnlyOpen_Tests.AssertPlainRows(db)).Should().Contain("cannot sync");
                File.ReadAllBytes(file.Filename).Should().Equal(original);
                (File.Exists(logName) ? new FileInfo(logName).Length : 0).Should().Be(0);

                power.DataFails = false;
                using (var converted = new LiteDatabase(file.Filename))
                {
                    UnsyncedReadOnlyOpen_Tests.AssertPlainRows(converted);
                    converted.GetCollection("rows").Insert(new BsonDocument { ["_id"] = 900002, ["value"] = 1 });
                    DurableLogFlush(converted).Should().BeTrue();
                }
                power.AfterPowerLoss(db => db.GetCollection("rows").FindById(900002) != null).Should().BeTrue();
            }
            finally { File.Delete(logName); }
        }

        /// <summary>
        /// Healthy storage pays the proof once per data header in the process, not per fresh engine:
        /// neither a shared connection's operations nor direct-mode open, commit and close add a data
        /// sync while the header is the one the latest successful data sync left. (Reading
        /// $database.walKept over a WAL that holds frames tries a data sync, as in direct mode.)
        /// </summary>
        [Fact]
        public void Data_file_is_proven_once_per_data_header()
        {
            using var file = new TempFile();
            Setup(file.Filename); // its closing checkpoint synced the data file
            using var power = new FilePowerLossModel(file.Filename);

            using (var engine = new SharedEngine(new EngineSettings { Filename = file.Filename }))
            using (var db = new LiteDatabase(engine, disposeOnClose: false))
            {
                db.CheckpointSize = 0;
                for (var id = 1; id <= 8; id++) Insert(db, id);
                power.DataSyncs.Should().Be(0, "every operation found the header the setup left durable");
                DurableLogFlush(db).Should().BeTrue();
            }
            power.DataSyncs.Should().Be(1, "only $database's walKept tried a data sync; closing added none");
            for (var id = 9; id <= 11; id++)
            {
                using var direct = new LiteDatabase(file.Filename);
                var atOpen = power.DataSyncs;
                Insert(direct, id);
                power.DataSyncs.Should().Be(atOpen, "the previous close's checkpoint synced this header");
                DurableLogFlush(direct).Should().BeTrue();
            }
        }

        /// <summary>
        /// The synced headers are remembered per path (DurableHeaders): a data file replaced at that
        /// path, here by an older copy restored over it, is proven again when its header differs
        /// from the one the latest sync there left. A replacement with that very header is taken as
        /// the synced file (the documented contract): whoever writes it, a restore included, must
        /// sync it, which File.Copy does not.
        /// </summary>
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void Data_file_replaced_at_its_path_is_proven_again_unless_it_has_the_synced_header(bool sameHeader)
        {
            using var file = new TempFile();
            Setup(file.Filename);
            var copy = File.ReadAllBytes(file.Filename);
            if (!sameHeader)
            {
                using var db = new LiteDatabase(file.Filename);
                Update(db, 1); // its closing checkpoint rotates the salt and syncs the new header
            }
            File.Exists(FileHelper.GetLogFile(file.Filename)).Should().BeFalse();
            File.WriteAllBytes(file.Filename, copy);

            using var power = new FilePowerLossModel(file.Filename);
            using var direct = new LiteDatabase(file.Filename);
            var atOpen = power.DataSyncs;
            Insert(direct, 100);
            (power.DataSyncs - atOpen).Should().Be(sameHeader ? 0 : 1);
            DurableLogFlush(direct).Should().BeTrue();
        }

        /// <summary>
        /// Another connection's checkpoint stops at its salt rotation, the rotated header in the OS
        /// cache only and the header journal kept. The next operation of a shared connection whose
        /// engines saw the old header durable must not report a commit durable on that header: while
        /// the data file cannot sync, its engine cannot recover (the journal cannot be retired) and
        /// opens read-only, so the write throws before any change, acknowledging nothing, and the
        /// connection keeps reading every commit. Once the data file syncs, it recovers, and its next
        /// commit is reported durable and survives the power loss: the data file's earlier "cannot
        /// sync" does not make the connection's commits non-durable (decision 4 of
        /// docs/decisions/durability-policy.md).
        /// </summary>
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void Shared_connection_proves_again_after_another_connection_left_its_header_unsynced(bool dataSyncsAgain)
        {
            using var file = new TempFile();
            Setup(file.Filename, index: true);
            var logName = FileHelper.GetLogFile(file.Filename);
            using var power = new FilePowerLossModel(file.Filename);
            try
            {
                using var engine = new SharedEngine(new EngineSettings { Filename = file.Filename });
                using var db = new LiteDatabase(engine, disposeOnClose: false);
                db.CheckpointSize = 0;
                Update(db, 1);
                DurableLogFlush(db).Should().BeTrue();

                var settings = new EngineSettings { Filename = file.Filename };
                settings.CheckpointStage = stage => { if (stage == "before-reclaim") power.DataFails = true; };
                using (var otherEngine = new SharedEngine(settings))
                using (var other = new LiteDatabase(otherEngine, disposeOnClose: false))
                {
                    Update(other, 2);
                    DurableLogFlush(other).Should().BeTrue();
                    Action checkpoint = () => other.Checkpoint();
                    checkpoint.Should().Throw<IOException>().WithMessage("The data file stopped syncing*");
                }
                power.DataFails.Should().BeTrue("the checkpoint reached its salt rotation");
                var kept = SyncPowerLossModel.ReadShared(logName);
                power.AfterPowerLoss(image => AssertRows(image, 2));
                FilePowerLossModel.Open((power.Capture().Data, kept), image => AssertRows(image, 2));

                power.DataFails = !dataSyncsAgain;
                if (!dataSyncsAgain)
                {
                    var data = SyncPowerLossModel.ReadShared(file.Filename);
                    var reason = UnsyncedReadOnlyOpen_Tests.ReadOnlyReason(db); // found out without a write
                    reason.Should().StartWith(UnsyncedReadOnlyOpen_Tests.RecoveryRefused);
                    UnsyncedReadOnlyOpen_Tests.AssertWriteRefused(() => Update(db, 3), reason);
                    AssertRows(db, 2); // the connection keeps reading
                    SyncPowerLossModel.ReadShared(file.Filename).Should().Equal(data, "the refused write changed nothing");
                    SyncPowerLossModel.ReadShared(logName).Should().Equal(kept, "the header journal stays");
                    power.AfterPowerLoss(image => AssertRows(image, 2));
                    power.DataFails = false; // the storage syncs again
                }
                Update(db, 3);
                DurableLogFlush(db).Should().BeTrue("the recovered header synced, and commits stay durable in the WAL");
                power.AfterPowerLoss(image => AssertRows(image, 3)); // a commit reported durable survives the power loss
            }
            finally { File.Delete(logName); }
        }

        /// <summary>
        /// The salt rotation case with the files passed as caller FileStreams: a fresh engine over
        /// them reported its commit durable without proving the data file. The checkpoint now stops
        /// with the WAL and its header journal kept, a fresh engine over the streams cannot recover
        /// while the data file cannot sync and opens read-only (reads every commit, refuses writes,
        /// writes nothing), and every durable commit survives the power loss; once the data file
        /// syncs, a fresh engine recovers and its durable commit survives too.
        /// </summary>
        [Fact]
        public void Fresh_engine_over_caller_file_streams_after_a_salt_rotation_whose_data_sync_failed()
        {
            using var file = new TempFile();
            Setup(file.Filename, index: true);
            var logName = FileHelper.GetLogFile(file.Filename);
            using var power = new SyncPowerLossModel(file.Filename);
            var settings = power.Settings();
            settings.CheckpointStage = stage => { if (stage == "before-reclaim") power.DataFails = true; };
            using (var first = new LiteDatabase(new LiteEngine(settings)))
            {
                first.CheckpointSize = 0;
                for (var value = 1; value <= 5; value++) Update(first, value);
                DurableLogFlush(first).Should().BeTrue();
                Action checkpoint = () => first.Checkpoint();
                checkpoint.Should().Throw<IOException>().WithMessage("The data file stopped syncing*");
            }
            var kept = SyncPowerLossModel.ReadShared(logName);
            var data = SyncPowerLossModel.ReadShared(file.Filename);

            using (var readOnly = new LiteDatabase(new LiteEngine(power.Settings()))) // the data file still cannot sync the rotated header
                UnsyncedReadOnlyOpen_Tests.AssertReadOnlyFallback(readOnly, UnsyncedReadOnlyOpen_Tests.RecoveryRefused, db => AssertRows(db, 5));
            SyncPowerLossModel.ReadShared(file.Filename).Should().Equal(data, "the read-only open wrote nothing");
            SyncPowerLossModel.ReadShared(logName).Should().Equal(kept);
            power.AssertAfterPowerLoss(Rows, 5);
            FilePowerLossModel.Open((power.Capture().Data, kept), db => AssertRows(db, 5));

            power.DataFails = false;
            using (var second = new LiteDatabase(new LiteEngine(power.Settings())))
            {
                AssertRows(second, 5);
                Update(second, 6);
                DurableLogFlush(second).Should().BeTrue();
            }
            power.AssertAfterPowerLoss(Rows, 6);
        }

        /// <summary>
        /// The data file syncs for the conversion's check at open, then stops before the conversion's
        /// own first sync: the conversion is still refused before it writes anything, and the open
        /// falls back to read-only, which reads every row, refuses writes, and syncs and writes nothing.
        /// </summary>
        [Fact]
        public void Conversion_whose_data_file_stops_syncing_after_its_check_opens_read_only_unchanged()
        {
            using var file = new TempFile();
            var original = Fixture("plain.db");
            File.WriteAllBytes(file.Filename, original);
            var logName = FileHelper.GetLogFile(file.Filename);
            var dataName = Path.GetFullPath(file.Filename);
            var dataSyncs = 0;
            NativeFileSync.SimulateErrno = path =>
                string.Equals(Path.GetFullPath(path), dataName, StringComparison.OrdinalIgnoreCase) && ++dataSyncs > 1 ? 22 : 0;
            try
            {
                using (var db = new LiteDatabase(file.Filename))
                    UnsyncedReadOnlyOpen_Tests.AssertReadOnlyFallback(db, UnsyncedReadOnlyOpen_Tests.ConversionRefused,
                        x => UnsyncedReadOnlyOpen_Tests.AssertPlainRows(x)).Should().Contain("cannot sync");
                dataSyncs.Should().Be(2, "the check at open synced, the conversion's own first sync did not");
            }
            finally { NativeFileSync.SimulateErrno = null; }
            File.ReadAllBytes(file.Filename).Should().Equal(original);
            (File.Exists(logName) ? new FileInfo(logName).Length : 0).Should().Be(0);
            File.Delete(logName);
        }

        /// <summary>
        /// A rebuild installs its replacement without the replacement's WAL. Where the data file
        /// cannot sync, the replacement's full checkpoint keeps that WAL (a data sync must cover the
        /// backfill), so installing the data file alone would lose every row: the rebuild is refused
        /// and the database is unchanged. The refusal comes before the engine closes, so the
        /// instance stays open and usable, and no replacement, backup or marker is left. Once the
        /// data file syncs, it rebuilds.
        /// </summary>
        [Fact]
        public void Rebuild_is_refused_where_only_the_data_file_cannot_sync()
        {
            using var file = new TempFile();
            Setup(file.Filename);
            var original = File.ReadAllBytes(file.Filename);
            NativeFileSync.SimulateErrno = path => path.EndsWith("-log.db", StringComparison.OrdinalIgnoreCase) ? 0 : 22;
            try
            {
                using var db = new LiteDatabase(file.Filename);
                Action rebuild = () => db.Rebuild();
                rebuild.Should().Throw<IOException>().WithMessage("Cannot rebuild this database now*");
                AssertRows(db, 0); // the instance is still open
                db.GetCollection("rows").Count().Should().Be(Rows);
            }
            finally { NativeFileSync.SimulateErrno = null; }
            File.ReadAllBytes(file.Filename).Should().Equal(original);
            Directory.GetFiles(Path.GetDirectoryName(file.Filename), Path.GetFileNameWithoutExtension(file.Filename) + "-*")
                .Should().BeEmpty("no replacement, backup or marker is left");

            using var reopened = new LiteDatabase(file.Filename);
            reopened.Rebuild();
            reopened.GetCollection("rows").FindAll().OrderBy(x => x["_id"].AsInt32).Should().BeEquivalentTo(
                Enumerable.Range(1, Rows).Select(id => MvccRetirementScenario.Document(id, 0)), o => o.WithStrictOrdering());
        }

        /// <summary>
        /// The data file syncs, but the rebuilt replacement's does not: the replacement is a database
        /// created where only its data file cannot sync, whose commits are durable in its WAL, the
        /// header anchored there (decisions 8 and 10 of docs/decisions/durability-policy.md), and whose
        /// checkpoint keeps that WAL, so the rebuild is refused after this engine closed for it. The
        /// refusal left the instance closed ("engine instance already disposed"); it now reopens the
        /// unchanged database, which reads and writes. Fails until decisions 8 and 10 are implemented:
        /// under the header rule they supersede (implementation note 1), the replacement's first commit
        /// throws that its header is not known to be on the device, which is not a refusal, and the
        /// instance stays closed.
        /// </summary>
        [Fact]
        public void Rebuild_refused_by_its_replacement_reopens_the_database()
        {
            using var file = new TempFile();
            Setup(file.Filename);
            try
            {
                using var db = new LiteDatabase(file.Filename);
                Update(db, 1);
                NativeFileSync.SimulateErrno = path => path.Contains("-temp") && !path.EndsWith("-log.db", StringComparison.OrdinalIgnoreCase) ? 22 : 0;
                Action rebuild = () => db.Rebuild();
                rebuild.Should().Throw<IOException>().WithMessage("Cannot rebuild this database now*");
                NativeFileSync.SimulateErrno = null;
                AssertRows(db, 1); // the instance reopened the unchanged database
                Update(db, 2);
                AssertRows(db, 2);
            }
            finally { NativeFileSync.SimulateErrno = null; }
            Directory.GetFiles(Path.GetDirectoryName(file.Filename), Path.GetFileNameWithoutExtension(file.Filename) + "-*")
                .Where(x => !x.EndsWith("-log.db", StringComparison.OrdinalIgnoreCase))
                .Should().BeEmpty("no replacement, backup or marker is left");
            using var reopened = new LiteDatabase(file.Filename);
            AssertRows(reopened, 2);
        }

        private const int Rows = 64;

        private static void Setup(string filename, bool index = false)
        {
            using var setup = new LiteDatabase(filename);
            setup.GetCollection("rows").Insert(Enumerable.Range(1, Rows).Select(id => MvccRetirementScenario.Document(id, 0)));
            if (index) setup.GetCollection("rows").EnsureIndex("value");
        }

        private static int AssertRows(LiteDatabase db, int value) => SyncPowerLossModel.AssertRows(db, Rows, value);

        private static void Update(LiteDatabase db, int value) =>
            db.GetCollection("rows").Upsert(Enumerable.Range(1, Rows).Select(id => MvccRetirementScenario.Document(id, value)));

        private static void Insert(LiteDatabase db, int id) =>
            db.GetCollection("log").Insert(new BsonDocument { ["_id"] = id });

        private static bool DurableLogFlush(LiteDatabase db) =>
            db.GetCollection("$database").FindAll().Single()["durableLogFlush"].AsBoolean;

        private static byte[] Fixture(string name)
        {
            using var resource = typeof(FreshEngineDurability_Tests).Assembly.GetManifestResourceStream(
                "LiteDB.Tests.Resources.IndexMigration_5_0_21.zip");
            using var zip = new ZipArchive(resource, ZipArchiveMode.Read);
            using var entry = zip.GetEntry(name).Open();
            using var bytes = new MemoryStream();
            entry.CopyTo(bytes);
            return bytes.ToArray();
        }
    }
}
#endif
