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
    /// Before its first log sync an engine whose data is a file (opened by the engine or passed as
    /// a FileStream) now proves the data file syncs; it skips that while the data header is one a
    /// successful sync in this process left.
    /// </summary>
    [Trait("Category", "RegressionSince5021")]
    [Collection(NativeFileSyncCollection.Name)]
    public class FreshEngineDurability_Tests
    {
        [Theory]
        [InlineData(false)]
        [InlineData(true)] // the data file syncs again for the fresh engine
        public void Fresh_engine_after_a_salt_rotation_whose_data_sync_failed(bool dataSyncsAgain)
        {
            using var file = new TempFile();
            Setup(file.Filename);
            using var power = new FilePowerLossModel(file.Filename);

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
                first.Checkpoint(); // the backfill syncs, the new salt's header does not
                DurableLogFlush(first).Should().BeFalse();
            }

            power.DataFails = !dataSyncsAgain;
            bool durable;
            using (var second = new LiteDatabase(file.Filename))
            {
                Update(second, 6);
                durable = DurableLogFlush(second);
            }
            durable.Should().Be(dataSyncsAgain, "a fresh engine proves the data file before its first durable commit");

            var values = power.AfterPowerLoss(Values);
            values.Should().HaveCount(1);
            if (durable) values.Single().Should().Be(6, "a commit reported durable survives the power loss");
            else values.Single().Should().BeOneOf(5, 6);
        }

        /// <summary>
        /// A 5.0.21 file (IndexMigration_5_0_21.zip, its WAL empty) on storage whose data file cannot
        /// sync while its WAL can. The converted header reached the OS cache only while the
        /// checksummed frames written after it became durable, so a power loss left them beside the
        /// legacy header. The conversion empties the log only after a data sync that succeeds, so it
        /// is now refused, the legacy file unchanged; once the data file syncs, it converts and
        /// commits are durable.
        /// </summary>
        [Fact]
        public void Conversion_is_refused_where_only_the_data_file_cannot_sync()
        {
            using var file = new TempFile();
            var original = Fixture("plain.db");
            File.WriteAllBytes(file.Filename, original);
            var logName = FileHelper.GetLogFile(file.Filename);
            using var power = new FilePowerLossModel(file.Filename) { DataFails = true };
            try
            {
                Action open = () => new LiteDatabase(file.Filename).Dispose();
                open.Should().Throw<IOException>().WithMessage("Cannot convert this legacy database now*cannot sync*");
                File.ReadAllBytes(file.Filename).Should().Equal(original);
                (File.Exists(logName) ? new FileInfo(logName).Length : 0).Should().Be(0);
                int rows;
                using (var legacy = new LiteDatabase($"Filename={file.Filename};ReadOnly=true;Legacy Index Scan=true"))
                    rows = legacy.GetCollection("rows").Count();
                rows.Should().BeGreaterThan(0);

                power.DataFails = false;
                using (var converted = new LiteDatabase(file.Filename))
                {
                    converted.GetCollection("rows").Count().Should().Be(rows);
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
        /// sync while the header is the one the latest successful data sync left.
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
                DurableLogFlush(db).Should().BeTrue();
            }
            power.DataSyncs.Should().Be(0, "every operation found the header the setup left durable");
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
        /// Another connection's checkpoint left a rotated header in the OS cache only: the next
        /// operation of a shared connection whose engines saw the old header durable proves again.
        /// </summary>
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void Shared_connection_proves_again_after_another_connection_left_its_header_unsynced(bool dataSyncsAgain)
        {
            using var file = new TempFile();
            Setup(file.Filename);
            using var power = new FilePowerLossModel(file.Filename);
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
                other.Checkpoint();
            }
            power.DataFails = !dataSyncsAgain;
            Update(db, 3);
            var durable = DurableLogFlush(db);
            durable.Should().Be(dataSyncsAgain);
            if (durable) power.AfterPowerLoss(Values).Should().Equal(new[] { 3 }, "a commit reported durable survives the power loss");
        }

        /// <summary>
        /// The salt rotation case with the files passed as caller FileStreams: a fresh engine over
        /// them reported its commit durable without proving the data file.
        /// </summary>
        [Fact]
        public void Fresh_engine_over_caller_file_streams_after_a_salt_rotation_whose_data_sync_failed()
        {
            using var file = new TempFile();
            Setup(file.Filename);
            using var power = new SyncPowerLossModel(file.Filename);
            var settings = power.Settings();
            settings.CheckpointStage = stage => { if (stage == "before-reclaim") power.DataFails = true; };
            using (var first = new LiteDatabase(new LiteEngine(settings)))
            {
                first.CheckpointSize = 0;
                for (var value = 1; value <= 5; value++) Update(first, value);
                first.Checkpoint();
                DurableLogFlush(first).Should().BeFalse();
            }

            using (var second = new LiteDatabase(new LiteEngine(power.Settings())))
            {
                Update(second, 6);
                DurableLogFlush(second).Should().BeFalse("the data file still cannot sync the rotated header");
            }
            power.AfterPowerLoss(Rows).Should().BeOneOf(5, 6);
        }

        /// <summary>
        /// Healthy storage: reusing retired WAL slots proves the data file like a commit does, once
        /// per data header, not with a data sync per shared operation.
        /// </summary>
        [Fact]
        public void Slot_reuse_adds_no_data_sync_per_shared_operation()
        {
            using var file = new TempFile();
            Setup(file.Filename);
            var logName = FileHelper.GetLogFile(file.Filename);
            using var power = new FilePowerLossModel(file.Filename);
            using var engine = new SharedEngine(new EngineSettings { Filename = file.Filename });
            using var db = new LiteDatabase(engine, disposeOnClose: false);
            db.CheckpointSize = 0;
            for (var value = 1; value <= 5; value++) Update(db, value);
            using (var reader = engine.Query("rows", new Query()))
            {
                reader.Read().Should().BeTrue("a live reader makes the checkpoint retire frames");
                var worker = new System.Threading.Thread(() =>
                {
                    for (var value = 6; value <= 9; value++) Update(db, value);
                    db.Checkpoint();
                });
                worker.Start();
                worker.Join();
            }
            var before = SyncPowerLossModel.ReadShared(logName);
            var syncs = power.DataSyncs;
            for (var value = 10; value < 15; value++) Update(db, value);
            var after = SyncPowerLossModel.ReadShared(logName);
            Enumerable.Range(0, before.Length / WalChecksum.FrameSize).Count(frame =>
                !before.Skip(frame * WalChecksum.FrameSize).Take(WalChecksum.FrameSize)
                    .SequenceEqual(after.Skip(frame * WalChecksum.FrameSize).Take(WalChecksum.FrameSize)))
                .Should().BeGreaterThan(0, "the operations reused retired slots");
            power.DataSyncs.Should().Be(syncs);
            DurableLogFlush(db).Should().BeTrue();
        }

        /// <summary>
        /// The data file syncs for the conversion's check at open, then stops before the conversion's
        /// own first sync: the conversion is still refused before it writes anything.
        /// </summary>
        [Fact]
        public void Conversion_whose_data_file_stops_syncing_after_its_check_is_refused_unchanged()
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
                Action open = () => new LiteDatabase(file.Filename).Dispose();
                open.Should().Throw<IOException>().WithMessage("Cannot convert this legacy database now*cannot sync*");
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
        /// and the database is unchanged. Once the data file syncs, it rebuilds.
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

        private const int Rows = 64;

        private static void Setup(string filename)
        {
            using var setup = new LiteDatabase(filename);
            setup.GetCollection("rows").Insert(Enumerable.Range(1, Rows).Select(id => MvccRetirementScenario.Document(id, 0)));
        }

        private static void Update(LiteDatabase db, int value) =>
            db.GetCollection("rows").Upsert(Enumerable.Range(1, Rows).Select(id => MvccRetirementScenario.Document(id, value)));

        private static void Insert(LiteDatabase db, int id) =>
            db.GetCollection("log").Insert(new BsonDocument { ["_id"] = id });

        private static int[] Values(LiteDatabase db) =>
            db.GetCollection("rows").FindAll().Select(x => x["value"].AsInt32).Distinct().ToArray();

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
