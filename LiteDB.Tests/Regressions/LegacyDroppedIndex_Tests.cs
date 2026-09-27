using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using FluentAssertions;
using Xunit;

namespace LiteDB.Tests.Regressions
{
    /// <summary>
    /// Regression since 5.0.21: CollectionPage now reads a vector-index count byte (and vector
    /// names/metadata) directly after the index list, with no file-version check (66ec615a1, #2678).
    /// 5.0.21's CollectionPage.UpdateBuffer never cleared the bytes after the index list, so a
    /// DropIndex leaves the tail of the old index entries on the page. HEAD interprets those stale
    /// bytes as vector metadata attached to a live index, and every insert then fails with
    /// "request page must be less or equals lastest page in data file". The writable open has
    /// already migrated the file, so 5.0.21 cannot open it any more either.
    ///
    /// Fixture DropIndex_5_0_21.zip was written by the LiteDB 5.0.21 NuGet package:
    /// collection "customers" with indexes Name, Age, CustomerId, 200 documents
    /// {_id: i, Name: "n"+i, Age: i%90, CustomerId: "C"+i}, then DropIndex("Age").
    /// The same file accepts insert/update/delete/EnsureIndex under 5.0.21.
    /// </summary>
    [Trait("Category", "RegressionSince5021")]
    public class LegacyDroppedIndex_Tests
    {
        [Fact]
        public void Collection_with_an_index_dropped_by_5_0_21_stays_writable()
        {
            using var file = new TempFile();
            File.WriteAllBytes(file.Filename, Fixture("customers.db"));

            using (var db = new LiteDatabase(file.Filename))
            {
                var col = db.GetCollection("customers");
                col.Count().Should().Be(200);

                col.Insert(new BsonDocument { ["_id"] = 1000, ["Name"] = "x", ["Age"] = 1, ["CustomerId"] = "C1000" });
                col.Update(new BsonDocument { ["_id"] = 1, ["Name"] = "y", ["Age"] = 2, ["CustomerId"] = "C1" }).Should().BeTrue();
                col.Delete(2).Should().BeTrue();
                col.EnsureIndex("Age");
                col.Count(Query.EQ("CustomerId", "C5")).Should().Be(1);
            }

            using (var db = new LiteDatabase(file.Filename))
            {
                db.GetCollection("customers").Count().Should().Be(200);
            }
        }

        private static byte[] Fixture(string name)
        {
            using var resource = typeof(LegacyDroppedIndex_Tests).Assembly.GetManifestResourceStream(
                "LiteDB.Tests.Resources.DropIndex_5_0_21.zip");
            using var zip = new ZipArchive(resource, ZipArchiveMode.Read);
            using var entry = zip.GetEntry(name).Open();
            using var bytes = new MemoryStream();
            entry.CopyTo(bytes);
            return bytes.ToArray();
        }
    }
}
