#if DEBUG || TESTING
using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using FluentAssertions;
using LiteDB.Engine;
using Xunit;

namespace LiteDB.Tests.Regressions
{
    /// <summary>
    /// Caller streams that cannot be written (see LegacyReadOnlyStream_Tests): the engine itself
    /// detects them, for LiteDatabase(Stream) and for EngineSettings streams alike, and never
    /// writes to either stream: not while opening, not in an error close, not to promote the
    /// file format for a compact write, and never through a checkpoint.
    /// </summary>
    [Trait("Category", "RegressionSince5021")]
    public class ReadOnlyStorageEngine_Tests
    {
        /// <summary>
        /// Regression since 5.0.21: EngineSettings streams (the only way to open an encrypted
        /// stream) over a 5.0.21 file failed with NotSupportedException after journaling the
        /// conversion into the caller's writable log. 5.0.21 read them and wrote nothing.
        /// </summary>
        [Theory]
        [InlineData(null)]
        [InlineData("migration-power-loss")]
        public void EngineSettings_streams_that_cannot_be_written_read_a_5_0_21_file_unchanged(string password)
        {
            var original = Entry("IndexMigration_5_0_21.zip", password == null ? "plain.db" : "encrypted.db");
            var originalLog = Entry("IndexMigration_5_0_21.zip", password == null ? "plain-log.db" : "encrypted-log.db");
            using var data = new MemoryStream(original, writable: false);
            using var log = Writable(originalLog);
            var settings = new EngineSettings { DataStream = data, LogStream = log, Password = password };

            using (var engine = new LiteEngine(settings))
            using (var db = new LiteDatabase(engine, disposeOnClose: false))
            {
                db.GetCollection("rows").Count().Should().BeGreaterThan(0);
                ReadOnly(db).Should().BeTrue("the file needs a migration the stream cannot take");
            }

            settings.ReadOnly.Should().BeFalse("the caller's settings are not changed");
            data.ToArray().Should().Equal(original);
            log.ToArray().Should().Equal(originalLog);
        }

        [Fact]
        public void Error_close_never_journals_into_the_log_of_storage_that_cannot_be_written()
        {
            var (original, _) = CurrentFile(CompactStorageMode.Auto);
            using var data = new MemoryStream(original, writable: false);
            using var log = new MemoryStream();

            using (var engine = new LiteEngine(new EngineSettings { DataStream = data, LogStream = log }))
            using (var db = new LiteDatabase(engine, disposeOnClose: false))
            {
                engine.SimulateDiskReadFail = page => throw LiteException.InvalidDatafileState("injected damage");
                Action query = () => db.GetCollection("rows").FindAll().ToList();
                query.Should().Throw<LiteException>().Which.ErrorCode.Should().Be(LiteException.INVALID_DATAFILE_STATE);
            }

            log.Length.Should().Be(0, "the invalid-state mark cannot be written, so it is not journaled either");
            data.ToArray().Should().Equal(original);
        }

        /// <summary>
        /// A v11 file (every migrated 5.0.21 file until its first compact write) must promote its
        /// format before a compact write, which storage that cannot be written cannot take: such
        /// an engine writes documents the v11 way, and they stay in the caller's log.
        /// </summary>
        [Fact]
        public void Writes_to_a_v11_file_over_a_read_only_data_stream_stay_in_the_log()
        {
            var (original, version) = CurrentFile(CompactStorageMode.Legacy);
            version.Should().Be(HeaderPage.INDEX_FILE_VERSION);
            using var data = new MemoryStream(original, writable: false);
            using var log = new MemoryStream();

            using (var db = new LiteDatabase(data, null, log))
            {
                db.GetCollection("shapes").Insert(Enumerable.Range(0, 40).Select(x => new BsonDocument
                {
                    ["_id"] = x, ["alpha"] = x, ["beta"] = new string('b', 60) + x,
                    ["gamma"] = new BsonDocument { ["g"] = x, ["longFieldNameForCompaction"] = "value" }
                }));
                db.GetCollection("shapes").Count().Should().Be(40);
            }
            data.ToArray().Should().Equal(original);

            using var reopened = new LiteDatabase(Writable(original), null, Writable(log.ToArray()));
            reopened.GetCollection("shapes").Count().Should().Be(40);
        }

        [Fact]
        public void Damaged_5_0_21_file_over_a_read_only_stream_reports_the_damage_and_changes_nothing()
        {
            var original = Entry("DamagedDocument_5_0_21.zip", "damaged.db");
            using var data = new MemoryStream(original, writable: false);
            using var log = new MemoryStream();

            using (var engine = new LiteEngine(new EngineSettings { DataStream = data, LogStream = log, AutoRebuild = true }))
            using (var db = new LiteDatabase(engine, disposeOnClose: false))
            {
                ReadOnly(db).Should().BeTrue("the file needs a migration the stream cannot take");
                db.GetCollection("c").FindById(1)["a"].AsString.Should().Be("keep-1");
                // Unmigrated indexes are not used: the scan reaches the damaged document.
                Action scan = () => db.GetCollection("c").FindAll().ToList();
                scan.Should().Throw<LiteException>().Which.ErrorCode.Should().Be(LiteException.INVALID_DATAFILE_STATE);
            }
            data.ToArray().Should().Equal(original);
            log.Length.Should().Be(0);
        }

        [Fact]
        public void Checkpoint_and_rebuild_of_read_only_streams_do_nothing_as_in_5_0_21()
        {
            var (current, _) = CurrentFile(CompactStorageMode.Auto);
            using (var db = new LiteDatabase(new MemoryStream(current, writable: false)))
            {
                db.Invoking(x => x.Checkpoint()).Should().NotThrow("an empty log has nothing to checkpoint");
                db.Rebuild().Should().Be(0);
            }

            using (var db = new LiteDatabase(new MemoryStream(Entry("DropIndex_5_0_21.zip", "customers.db"), writable: false)))
            {
                ReadOnly(db).Should().BeTrue();
                db.Rebuild().Should().Be(0, "a stream is never rebuilt");
            }
        }

        private static (byte[] data, byte version) CurrentFile(CompactStorageMode mode)
        {
            var data = new MemoryStream();
            var log = new MemoryStream();
            using (var db = new LiteDatabase(new LiteEngine(new EngineSettings { DataStream = data, LogStream = log, CompactStorage = mode })))
            {
                var rows = db.GetCollection("rows");
                rows.EnsureIndex("value");
                rows.Insert(Enumerable.Range(1, 50).Select(i => new BsonDocument { ["_id"] = i, ["value"] = i % 7, ["p"] = new string('x', 300) }));
                db.Checkpoint();
            }
            log.Length.Should().Be(0);
            var bytes = data.ToArray();
            return (bytes, bytes[HeaderPage.P_FILE_VERSION]);
        }

        private static MemoryStream Writable(byte[] bytes)
        {
            var stream = new MemoryStream();
            stream.Write(bytes, 0, bytes.Length);
            stream.Position = 0;
            return stream;
        }

        private static bool ReadOnly(LiteDatabase db) =>
            db.GetCollection("$database").FindAll().Single()["readOnly"].AsBoolean;

        // IndexMigration_5_0_21.zip is an embedded resource on dev; later fixtures live in LiteDB-Artifacts.
        private static byte[] Entry(string fixture, string name)
        {
            using var zip = fixture == "IndexMigration_5_0_21.zip"
                ? new ZipArchive(typeof(ReadOnlyStorageEngine_Tests).Assembly.GetManifestResourceStream("LiteDB.Tests.Resources." + fixture), ZipArchiveMode.Read)
                : new ZipArchive(File.OpenRead(ArtifactFixtures.Path(fixture)), ZipArchiveMode.Read);
            using var entry = zip.GetEntry(name).Open();
            using var bytes = new MemoryStream();
            entry.CopyTo(bytes);
            return bytes.ToArray();
        }
    }
}
#endif
