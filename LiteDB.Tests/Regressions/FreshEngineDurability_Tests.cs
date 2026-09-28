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
    /// Before its first durable commit a file-backed engine now proves the data file syncs; a
    /// shared connection skips that while the data header is the one it last saw durable.
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
        /// sync while its WAL can: the converted header would reach the OS cache only, while the
        /// checksummed frames written after it became durable, so a power loss left them beside the
        /// legacy header. Conversion is refused, changing neither file, and runs once the data file syncs.
        /// </summary>
        [Fact]
        public void Conversion_is_refused_while_only_the_data_file_cannot_sync()
        {
            using var file = new TempFile();
            var original = Fixture("plain.db");
            File.WriteAllBytes(file.Filename, original);
            var logName = FileHelper.GetLogFile(file.Filename);
            using var power = new FilePowerLossModel(file.Filename) { DataFails = true };
            try
            {
                Action open = () => new LiteDatabase(file.Filename).Dispose();
                open.Should().Throw<IOException>().WithMessage("Cannot convert this legacy database*data file cannot be synced*");
                File.ReadAllBytes(file.Filename).Should().Equal(original);
                (!File.Exists(logName) || new FileInfo(logName).Length == 0).Should().BeTrue("the refusal writes nothing to the log");

                power.DataFails = false;
                using (var converted = new LiteDatabase(file.Filename))
                {
                    converted.GetCollection("rows").Insert(new BsonDocument { ["_id"] = 900002, ["value"] = 1 });
                    DurableLogFlush(converted).Should().BeTrue();
                }
                power.AfterPowerLoss(db => db.GetCollection("rows").FindById(900002) != null).Should().BeTrue();
            }
            finally { File.Delete(logName); }
        }

        /// <summary>
        /// Healthy storage: a shared connection syncs the data file once, not on every operation's
        /// fresh engine, until another connection changes the data header (a full checkpoint).
        /// </summary>
        [Fact]
        public void Shared_connection_proves_the_data_file_once_per_data_header()
        {
            using var file = new TempFile();
            Setup(file.Filename);
            using var power = new FilePowerLossModel(file.Filename);

            using var engine = new SharedEngine(new EngineSettings { Filename = file.Filename });
            using var db = new LiteDatabase(engine, disposeOnClose: false);
            db.CheckpointSize = 0;
            Insert(db, 1);
            var afterFirst = power.DataSyncs;
            afterFirst.Should().Be(1, "the first operation proves the data file");
            for (var id = 2; id <= 8; id++) Insert(db, id);
            power.DataSyncs.Should().Be(afterFirst, "later operations find the data header they saw durable");

            using (var other = new LiteDatabase($"Filename={file.Filename};Connection=shared"))
            {
                other.Checkpoint(); // rotates the WAL salt in the data header
            }
            var afterCheckpoint = power.DataSyncs;
            Insert(db, 9);
            power.DataSyncs.Should().Be(afterCheckpoint + 1, "a changed data header is proven again");
            Insert(db, 10);
            power.DataSyncs.Should().Be(afterCheckpoint + 1);
            DurableLogFlush(db).Should().BeTrue();
        }

        /// <summary>
        /// A rebuild installs its replacement without the replacement's WAL. Where only the data file
        /// cannot sync, that WAL is kept, so the rebuilt file could never be durable: the rebuild is
        /// refused and the database is left as it was.
        /// </summary>
        [Fact]
        public void Rebuild_is_refused_where_only_the_data_file_cannot_sync()
        {
            using var file = new TempFile();
            Setup(file.Filename);
            var temp = FileHelper.GetSuffixFile(file.Filename, "-temp", true);
            NativeFileSync.SimulateErrno = path => path.EndsWith("-log.db", StringComparison.OrdinalIgnoreCase) ? 0 : 22;
            try
            {
                using var db = new LiteDatabase(file.Filename);
                Action rebuild = () => db.Rebuild();
                rebuild.Should().Throw<IOException>().WithMessage("Cannot rebuild this database*");
                File.Exists(temp).Should().BeFalse();
                File.Exists(FileHelper.GetLogFile(temp)).Should().BeFalse();
            }
            finally { NativeFileSync.SimulateErrno = null; }

            using var reopened = new LiteDatabase(file.Filename);
            reopened.GetCollection("rows").Count().Should().Be(Rows);
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
