using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using FluentAssertions;
using LiteDB.Engine;
using LiteDB.Tests.Engine;
using Xunit;

namespace LiteDB.Tests.Regressions
{
    /// <summary>
    /// Regression since 5.0.21: <c>new LiteDatabase(stream)</c> over a non-writable stream (a
    /// FileStream opened with FileAccess.Read, an embedded resource, a read-only share) opens a
    /// writable engine, and every writable open of a 5.0.21 file migrates it (index ordering and
    /// checksum conversion), which writes to the stream: NotSupportedException. This constructor
    /// has no ReadOnly/LegacyIndexScan switch. 5.0.21 read the same stream without writing.
    /// Uses the real 5.0.21 fixture DropIndex_5_0_21.zip (checked with the 5.0.21 package).
    ///
    /// Only an open that would change the file falls back to read-only: over a current file the
    /// engine stays writable, so explicit transactions and writes that change nothing work as in
    /// 5.0.21 (checked with the 5.0.21 package: BeginTrans/Commit and a DeleteMany matching nothing).
    /// </summary>
    [Trait("Category", "RegressionSince5021")]
    public class LegacyReadOnlyStream_Tests
    {
        [Fact]
        public void Read_only_stream_of_a_5_0_21_database_can_be_queried()
        {
            using var file = new TempFile();
            using (var resource = typeof(LegacyReadOnlyStream_Tests).Assembly.GetManifestResourceStream(
                "LiteDB.Tests.Resources.DropIndex_5_0_21.zip"))
            using (var zip = new ZipArchive(resource, ZipArchiveMode.Read))
            using (var entry = zip.GetEntry("customers.db").Open())
            using (var output = File.Create(file.Filename))
            {
                entry.CopyTo(output);
            }

            var original = File.ReadAllBytes(file.Filename);
            using (var stream = new FileStream(file.Filename, FileMode.Open, FileAccess.Read))
            using (var db = new LiteDatabase(stream))
            {
                var customers = db.GetCollection("customers");
                customers.Count().Should().Be(200);
                customers.Count(Query.EQ("CustomerId", "C5")).Should().Be(1);
                customers.Count(Query.GT("Name", "n9")).Should().Be(10); // n90..n99
                Action write = () => customers.Insert(new BsonDocument { ["_id"] = 1000 });
                write.Should().Throw<Exception>();
                ReadOnly(db).Should().BeTrue("the file needs a migration the stream cannot take");
            }
            File.ReadAllBytes(file.Filename).Should().Equal(original, "a read-only stream must never be migrated or written");
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)] // ends in a partial page, which a writable open would trim
        public void Read_only_stream_of_a_current_database_keeps_transactions_that_change_nothing(bool partialTail)
        {
            using var file = new TempFile();
            using (var db = new LiteDatabase(file.Filename))
            {
                db.GetCollection("items").Insert(new BsonDocument { ["_id"] = 1, ["name"] = "a" });
                db.GetCollection("items").EnsureIndex("name");
            }
            if (partialTail) File.AppendAllText(file.Filename, "partial");

            var original = File.ReadAllBytes(file.Filename);
            using (var stream = new FileStream(file.Filename, FileMode.Open, FileAccess.Read))
            using (var db = new LiteDatabase(stream))
            {
                var items = db.GetCollection("items");
                items.FindOne(Query.EQ("name", "a"))["_id"].AsInt32.Should().Be(1);
                ReadOnly(db).Should().Be(partialTail);
                if (!partialTail)
                {
                    db.BeginTrans().Should().BeTrue();
                    (items.FindById(1) != null).Should().BeTrue();
                    items.DeleteMany(Query.EQ("name", "missing")).Should().Be(0);
                    db.Commit().Should().BeTrue();
                    db.BeginTrans().Should().BeTrue();
                    db.Rollback().Should().BeTrue();
                }
            }
            File.ReadAllBytes(file.Filename).Should().Equal(original, "nothing was changed");
        }

        /// <summary>
        /// A 5.0.21 file over a non-writable stream opens read-only: explicit transactions work as
        /// in 5.0.21, but write operations are rejected, even ones that would change nothing,
        /// because its unmigrated indexes cannot serve them (5.0.21 kept such changes only in
        /// memory or in the log).
        /// </summary>
        [Fact]
        public void Read_only_stream_of_a_5_0_21_database_accepts_transactions_and_rejects_writes()
        {
            var original = DropIndexFixture("customers.db");
            using var stream = new MemoryStream(original, writable: false);
            using (var db = new LiteDatabase(stream))
            {
                var customers = db.GetCollection("customers");
                db.BeginTrans().Should().BeTrue();
                customers.Count(Query.EQ("CustomerId", "C5")).Should().Be(1);
                db.Commit().Should().BeTrue();
                db.BeginTrans().Should().BeTrue();
                db.Rollback().Should().BeTrue();
                Action delete = () => customers.DeleteMany(Query.EQ("Name", "missing"));
                delete.Should().Throw<IOException>().WithMessage("*read-only*");
                customers.Count().Should().Be(200);
            }
            stream.ToArray().Should().Equal(original);
        }

        /// <summary>
        /// Every crash image of a 5.0.21 migration (and a torn format-promotion header) needs
        /// recovery or repair. When its data or log stream cannot be written it opens read-only as
        /// it is: no recovery step may change either stream first.
        /// </summary>
        [Theory]
        [InlineData(false, true)]
        [InlineData(false, false)]
        [InlineData(true, false)]
        public void Crash_images_over_non_writable_streams_open_without_any_change(bool writableData, bool writableLog)
        {
            var images = MigrationCrashImages().ToList();
            byte[] promotedData = null, promotedLog = null;
            PromotionPowerLossScenario.Run(null, false, "promotion-before-header-write", tornPrefix: 59, damage: true,
                inspectFiles: (data, log) => { promotedData = data; promotedLog = log; });
            images.Add(("torn promotion header", promotedData, promotedLog, "rows"));

            foreach (var (name, dataBytes, logBytes, collection) in images)
            {
                using var data = writableData ? CopyOf(dataBytes) : new MemoryStream((byte[])dataBytes.Clone(), writable: false);
                using var log = writableLog ? CopyOf(logBytes) : new MemoryStream((byte[])logBytes.Clone(), writable: false);
                int expected;
                using (var reference = new LiteEngine(new EngineSettings
                {
                    DataStream = new MemoryStream((byte[])dataBytes.Clone(), false), LogStream = new MemoryStream((byte[])logBytes.Clone(), false),
                    ReadOnly = true, LegacyIndexScan = true
                }))
                using (var referenceDb = new LiteDatabase(reference))
                {
                    expected = referenceDb.GetCollection(collection).Count();
                }

                using (var db = new LiteDatabase(data, null, log))
                {
                    db.GetCollection(collection).Count().Should().Be(expected, name);
                }
                data.ToArray().Should().Equal(dataBytes, name);
                log.ToArray().Should().Equal(logBytes, name);
            }
        }

        private static IEnumerable<(string, byte[], byte[], string)> MigrationCrashImages()
        {
            using var resource = typeof(LegacyReadOnlyStream_Tests).Assembly.GetManifestResourceStream(
                "LiteDB.Tests.Resources.IndexMigration_5_0_21.zip");
            using var zip = new ZipArchive(resource, ZipArchiveMode.Read);
            var original = Entry(zip, "plain.db");
            var originalLog = Entry(zip, "plain-log.db");
            using var device = new IndexMigrationCrashDevice(original, originalLog);
            using (var db = new LiteDatabase(new LiteEngine(new EngineSettings { DataStream = device.Data, LogStream = device.Log, TransactionPageLimit = 4 })))
            {
                db.GetCollection("rows").Count();
                device.Stage = "checkpoint";
                db.Checkpoint();
                device.Armed = false;
            }
            device.Images.Should().HaveCountGreaterThan(10);
            return device.Images.Select(image => (image.Event, image.Data, image.Log, "rows")).ToList();
        }

        private static byte[] Entry(ZipArchive zip, string name)
        {
            using var entry = zip.GetEntry(name).Open();
            using var bytes = new MemoryStream();
            entry.CopyTo(bytes);
            return bytes.ToArray();
        }

        private static MemoryStream CopyOf(byte[] bytes)
        {
            var stream = new MemoryStream();
            stream.Write(bytes, 0, bytes.Length);
            stream.Position = 0;
            return stream;
        }

        private static byte[] DropIndexFixture(string name)
        {
            using var resource = typeof(LegacyReadOnlyStream_Tests).Assembly.GetManifestResourceStream(
                "LiteDB.Tests.Resources.DropIndex_5_0_21.zip");
            using var zip = new ZipArchive(resource, ZipArchiveMode.Read);
            return Entry(zip, name);
        }

        private static bool ReadOnly(LiteDatabase db) =>
            db.GetCollection("$database").FindAll().Single()["readOnly"].AsBoolean;
    }
}
