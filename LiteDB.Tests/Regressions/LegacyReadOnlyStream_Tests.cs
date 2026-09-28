using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using FluentAssertions;
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

        private static bool ReadOnly(LiteDatabase db) =>
            db.GetCollection("$database").FindAll().Single()["readOnly"].AsBoolean;
    }
}
