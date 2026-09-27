using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using FluentAssertions;
using LiteDB.Engine;
using LiteDB.Internals;
using LiteDB.Tests.Engine;
using LiteDB.Vector;
using Xunit;
using static LiteDB.Constants;

namespace LiteDB.Tests.Regressions
{
    /// <summary>
    /// Regression since 5.0.21: CollectionPage read a vector-index count byte (and vector
    /// names/metadata) directly after the index list, with no file-version check (66ec615a1, #2678).
    /// 5.0.21's CollectionPage.UpdateBuffer never cleared the bytes after the index list, so a
    /// DropIndex leaves the tail of the old index entries on the page. HEAD interpreted those stale
    /// bytes as vector metadata attached to a live index, and every insert then failed with
    /// "request page must be less or equals lastest page in data file" - after the writable open
    /// had already migrated the file, so 5.0.21 could not open it any more either.
    ///
    /// Fixture DropIndex_5_0_21.zip was written by the LiteDB 5.0.21 NuGet package:
    /// customers.db: collection "customers" with indexes Name, Age, CustomerId, 200 documents
    /// {_id: i, Name: "n"+i, Age: i%90, CustomerId: "C"+i}, then DropIndex("Age").
    /// items-a.db / items-b.db: collection "items", 50 documents {_id: i, field: field+i}, with
    /// indexes CreatedAt,Phone,Status (drop CreatedAt,Phone) and LastLogin,Score,CustomerId,
    /// Country,Status (drop CustomerId,LastLogin,Country,Score) - two of 40 random layouts that
    /// failed. All three files accept insert/update/delete/EnsureIndex under 5.0.21.
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
                db.GetCollection("customers").Count(Query.EQ("Age", 2)).Should().Be(3); // 92, 182 and updated 1
            }
        }

        [Theory]
        [InlineData("items-a.db", "CreatedAt,Phone,Status")]
        [InlineData("items-b.db", "LastLogin,Score,CustomerId,Country,Status")]
        public void Other_5_0_21_drop_layouts_stay_writable(string name, string fields)
        {
            using var file = new TempFile();
            File.WriteAllBytes(file.Filename, Fixture(name));
            var names = fields.Split(',');
            BsonDocument Doc(int id)
            {
                var doc = new BsonDocument { ["_id"] = id };
                foreach (var field in names) doc[field] = field + "x" + id;
                return doc;
            }

            using (var db = new LiteDatabase(file.Filename))
            {
                var col = db.GetCollection("items");
                col.Insert(Doc(1000));
                col.Update(Doc(1)).Should().BeTrue();
                col.Delete(2).Should().BeTrue();
                col.EnsureIndex(names[0]);
            }

            using (var db = new LiteDatabase(file.Filename))
            {
                var col = db.GetCollection("items");
                col.Count().Should().Be(50);
                col.Count(Query.EQ(names[0], names[0] + "x1")).Should().Be(1);
            }
        }

        [Fact]
        public void Every_interrupted_migration_image_recovers_writable()
        {
            var original = Fixture("customers.db");
            using var device = new IndexMigrationCrashDevice(original, Array.Empty<byte>());
            using (var db = new LiteDatabase(new LiteEngine(new EngineSettings { DataStream = device.Data, LogStream = device.Log })))
            {
                db.GetCollection("customers").Count().Should().Be(200);
            }
            device.Armed = false;

            // The window this fix protects: a converted (Mixed, v8-origin) header while the
            // collection page is still the unmarked page 5.0.21 wrote.
            device.Images.Should().Contain(image => image.Data[HeaderPage.P_FILE_VERSION] >= HeaderPage.CHECKSUM_FILE_VERSION &&
                image.Data[DataChecksumPolicy.LegacyVersionPosition] == HeaderPage.FILE_VERSION &&
                HasUnmarkedCollectionPage(image.Data));

            foreach (var image in device.Images)
            {
                using var data = ChecksumTestFiles.Copy(image.Data);
                using var log = ChecksumTestFiles.Copy(image.Log);
                using (var db = new LiteDatabase(new LiteEngine(new EngineSettings { DataStream = data, LogStream = log })))
                {
                    var col = db.GetCollection("customers");
                    col.Insert(new BsonDocument { ["_id"] = 1000, ["Name"] = "x", ["Age"] = 1, ["CustomerId"] = "C1000" });
                    col.Delete(3).Should().BeTrue(image.Event);
                    col.Count().Should().Be(200, image.Event);
                }
                using (var db = new LiteDatabase(new LiteEngine(new EngineSettings { DataStream = data, LogStream = log })))
                {
                    db.GetCollection("customers").Count(Query.EQ("CustomerId", "C1000")).Should().Be(1, image.Event);
                }
            }
        }

        [Fact]
        public void Vector_index_added_after_migration_keeps_its_metadata()
        {
            using var file = new TempFile();
            File.WriteAllBytes(file.Filename, Fixture("customers.db"));

            using (var db = new LiteDatabase(file.Filename))
            {
                var col = db.GetCollection("customers");
                for (var id = 1; id <= 20; id++)
                {
                    var doc = col.FindById(id);
                    doc["Embedding"] = new BsonVector(new[] { (float)id, 1f });
                    col.Update(doc).Should().BeTrue();
                }
                col.EnsureIndex("embedding", "$.Embedding", new VectorIndexOptions(2));
                db.Checkpoint();
            }

            var header = File.ReadAllBytes(file.Filename).Take(PAGE_SIZE).ToArray();
            header[HeaderPage.P_FILE_VERSION].Should().BeGreaterOrEqualTo(HeaderPage.CHECKSUM_FILE_VERSION);
            header[DataChecksumPolicy.LegacyVersionPosition].Should().Be(HeaderPage.FILE_VERSION);

            using (var db = new LiteDatabase(file.Filename))
            {
                db.GetCollection("$indexes").Find(Query.EQ("name", "embedding")).Should().ContainSingle();
                var nearest = db.GetCollection("customers").Query().TopKNear(BsonExpression.Create("$.Embedding"), new[] { 20f, 1f }, 1);
                nearest.GetPlan()["index"]["name"].AsString.Should().Be("embedding", "the reopened page must keep the vector metadata");
                nearest.ToArray().Single()["_id"].AsInt32.Should().Be(20);
                db.GetCollection("customers").Insert(new BsonDocument { ["_id"] = 2000, ["Embedding"] = new BsonVector(new[] { 3f, 1f }) });
            }
        }

        [Theory]
        [InlineData(8, 0, false)]
        [InlineData(9, 0, true)]
        [InlineData(10, 8, false)]
        [InlineData(10, 9, true)]
        [InlineData(13, 8, false)]
        [InlineData(13, 0, true)]
        public void Unmarked_pages_carry_a_vector_section_only_in_files_that_stored_vectors(byte version, byte legacy, bool expected)
        {
            CollectionPage.UnmarkedPagesHaveVectorSection(version, legacy).Should().Be(expected);
        }

        [Theory]
        [InlineData(DataChecksumPolicy.MixedMarker, 7)]
        [InlineData(DataChecksumPolicy.MixedMarker, 10)]
        [InlineData(DataChecksumPolicy.CompleteMarker, 8)]
        public void Unknown_legacy_version_bytes_fail_closed(byte coverage, byte legacy)
        {
            var header = new PageBuffer(new byte[PAGE_SIZE], 0, 0);
            new HeaderPage(header, 0) { LastPageID = 10 }.UpdateBuffer();
            header[DataChecksumPolicy.CoveragePosition] = coverage;
            if (coverage == DataChecksumPolicy.MixedMarker) header.Write(5u, DataChecksumPolicy.LegacyBoundaryPosition);
            header[DataChecksumPolicy.LegacyVersionPosition] = legacy;

            Assert.Throws<PageChecksumException>(() => new DataChecksumPolicy().Load(header));
        }

        private static bool HasUnmarkedCollectionPage(byte[] data)
        {
            for (var offset = PAGE_SIZE; offset + PAGE_SIZE <= data.Length; offset += PAGE_SIZE)
            {
                if (data[offset + BasePage.P_PAGE_TYPE] == (byte)PageType.Collection &&
                    BitConverter.ToUInt32(data, offset + 68) != 0x3156444C) return true;
            }
            return false;
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
