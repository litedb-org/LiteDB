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
    /// Fixture DamagedDocument_5_0_21.zip: written by the LiteDB 5.0.21 package, collection "c" with
    /// {_id: 1..3, a: "keep-i", b: "tail-i-zzz..."}; then the BSON length of string field "b" of
    /// document 2 was overwritten with 0x7FFFFFF0 (one damaged value inside one document).
    ///
    /// 5.0.21: the database opens, documents 1 and 3 are readable, and db.Rebuild() keeps all three
    /// documents - document 2 with the fields read before the damage ({_id: 2, a: "keep-2"}) - and
    /// records one _rebuild_errors entry. A writable open of this version must migrate every index
    /// from its documents, which the damage prevents; it cannot stamp unverified indexes as migrated.
    /// </summary>
    [Trait("Category", "RegressionSince5021")]
    public class LegacyDamagedDocument_Tests
    {
        /// <summary>
        /// Regression: the failed migration surfaced only an internal ENSURE message and AutoRebuild
        /// could repair the file only on a later open. The failure now names the damaged collection
        /// and the remedies, and changes nothing but the rebuild mark (which 5.0.21 ignores unless
        /// its own AutoRebuild is set).
        /// </summary>
        [Fact]
        public void Default_open_reports_the_damage_and_only_marks_the_file_for_rebuild()
        {
            using var file = new TempFile();
            var original = Fixture();
            File.WriteAllBytes(file.Filename, original);

            Action open = () => new LiteDatabase(file.Filename).Dispose();
            var error = open.Should().Throw<LiteException>().Which;
            error.ErrorCode.Should().Be(LiteException.INVALID_DATAFILE_STATE);
            error.Message.Should().Contain("Collection 'c'").And.Contain("auto-rebuild=true").And.Contain("legacy index scan=true");

            var after = File.ReadAllBytes(file.Filename);
            after[HeaderPage.P_INVALID_DATAFILE_STATE].Should().Be(1);
            after[HeaderPage.P_INVALID_DATAFILE_STATE] = original[HeaderPage.P_INVALID_DATAFILE_STATE];
            after.Should().Equal(original, "the refused migration must not change anything else");
            File.Exists(FileHelper.GetLogFile(file.Filename)).Should().BeFalse();

            using var db = new LiteDatabase($"Filename={file.Filename};Auto-Rebuild=true");
            AssertSalvaged(db);
        }

        [Fact]
        public void Auto_rebuild_repairs_the_damage_on_the_first_open()
        {
            using var file = new TempFile();
            File.WriteAllBytes(file.Filename, Fixture());

            using (var db = new LiteDatabase($"Filename={file.Filename};Auto-Rebuild=true"))
            {
                AssertSalvaged(db);
                db.GetCollection("c").Insert(new BsonDocument { ["_id"] = 4 });
            }

            File.Exists(FileHelper.GetSuffixFile(file.Filename, "-backup", false)).Should().BeTrue();
            using var reopened = new LiteDatabase(file.Filename);
            reopened.GetCollection("c").Count().Should().Be(4);
        }

        [Fact]
        public void Read_only_legacy_scan_surfaces_the_damage_without_changing_the_file()
        {
            using var file = new TempFile();
            var original = Fixture();
            File.WriteAllBytes(file.Filename, original);

            using (var db = new LiteDatabase($"Filename={file.Filename};ReadOnly=true;Legacy Index Scan=true"))
            {
                // Unmigrated indexes are not trusted, so every query scans the collection; the
                // damaged document is reported (and stops the engine), never skipped.
                Action scan = () => db.GetCollection("c").FindAll().ToList();
                scan.Should().Throw<LiteException>().Which.ErrorCode.Should().Be(LiteException.INVALID_DATAFILE_STATE);
            }
            File.ReadAllBytes(file.Filename).Should().Equal(original, "a read-only open never marks or changes the file");
        }

        /// <summary>
        /// Regression: FileReaderV8 discarded a document whose BSON read failed
        /// (FileReaderV8.Documents.cs, 05d34f058); 5.0.21 yielded the partial document.
        /// </summary>
        [Fact]
        public void Rebuild_keeps_the_readable_prefix_of_a_damaged_document()
        {
            using var file = new TempFile();
            var bytes = Fixture();
            bytes[HeaderPage.P_INVALID_DATAFILE_STATE] = 1; // request rebuild on open (AutoRebuild)
            File.WriteAllBytes(file.Filename, bytes);

            using var db = new LiteDatabase($"Filename={file.Filename};Auto-Rebuild=true");
            AssertSalvaged(db);
        }

        private static void AssertSalvaged(LiteDatabase db)
        {
            var col = db.GetCollection("c");
            col.Count().Should().Be(3);
            col.FindById(1)["b"].AsString.Should().StartWith("tail-1-");
            var partial = col.FindById(2);
            partial["a"].AsString.Should().Be("keep-2");
            partial.ContainsKey("b").Should().BeFalse();
            db.GetCollection("_rebuild_errors").Count().Should().BeGreaterOrEqualTo(1);
        }

        private static byte[] Fixture()
        {
            using var resource = typeof(LegacyDamagedDocument_Tests).Assembly.GetManifestResourceStream(
                "LiteDB.Tests.Resources.DamagedDocument_5_0_21.zip");
            using var zip = new ZipArchive(resource, ZipArchiveMode.Read);
            using var entry = zip.GetEntry("damaged.db").Open();
            using var bytes = new MemoryStream();
            entry.CopyTo(bytes);
            return bytes.ToArray();
        }
    }
}
