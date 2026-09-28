using System;
using System.IO;
using System.IO.Compression;
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
            }
            File.ReadAllBytes(file.Filename).Should().Equal(original, "a read-only stream must never be migrated or written");
        }
    }
}
