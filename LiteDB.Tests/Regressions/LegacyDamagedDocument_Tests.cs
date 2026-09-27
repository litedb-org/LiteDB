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
    /// records one _rebuild_errors entry.
    /// </summary>
    [Trait("Category", "RegressionSince5021")]
    public class LegacyDamagedDocument_Tests
    {
        /// <summary>
        /// Regression: the writable open runs the index-ordering migration, which reads every
        /// document (IndexMigration.GetMigrationKeys) and fails on the damaged one; the header is
        /// then marked invalid. AutoRebuild does not run on that first open, so with default settings
        /// the whole database, including every undamaged collection, cannot be opened at all.
        /// </summary>
        [Fact]
        public void Database_with_one_damaged_document_still_opens_and_serves_the_other_documents()
        {
            using var file = new TempFile();
            File.WriteAllBytes(file.Filename, Fixture());

            using var db = new LiteDatabase(file.Filename);
            var col = db.GetCollection("c");
            col.FindById(1)["a"].AsString.Should().Be("keep-1");
            col.FindById(3)["a"].AsString.Should().Be("keep-3");
        }

        /// <summary>
        /// Regression: FileReaderV8 now discards a document whose BSON read fails
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
            var col = db.GetCollection("c");
            col.Count().Should().Be(3);
            col.FindById(2)["a"].AsString.Should().Be("keep-2");
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
