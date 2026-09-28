using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using FluentAssertions;
using LiteDB.Engine;
using LiteDB.Vector;
using Xunit;

namespace LiteDB.Tests.Regressions
{
    /// <summary>
    /// Fixture Vectors_6_0_0_prerelease_114.zip was written by the published LiteDB
    /// 6.0.0-prerelease.114 package, which stores vector indexes in FILE_VERSION 8 files:
    /// vectors.db (and vectors-encrypted.db, password "vector-secret") hold collection "docs"
    /// with 40 documents {_id: i, name: "d"+i, Embedding: [i, 1]}, index "name" and vector index
    /// "embedding" on $.Embedding (2 dimensions), and collection "computed" with 20 documents
    /// {_id: i, Embedding: [1, i]} (every fifth Embedding null) and vector index "coalesced" on
    /// COALESCE($.Embedding, [0, 0]).
    ///
    /// The collection-page rule that ignores a 5.0.21 stale tail after the index list must still
    /// read the vector section these files store there.
    /// </summary>
    [Trait("Category", "RegressionSince5021")]
    public class PrereleaseVectorFile_Tests
    {
        [Theory]
        [InlineData("vectors.db", null)]
        [InlineData("vectors-encrypted.db", "vector-secret")]
        public void Prerelease_vector_indexes_stay_usable_after_the_writable_open(string name, string password)
        {
            using var file = new TempFile();
            File.WriteAllBytes(file.Filename, Fixture(name));
            var connection = $"Filename={file.Filename}" + (password == null ? "" : $";Password={password}");

            using (var db = new LiteDatabase(connection))
            {
                AssertVectorIndexes(db);
                var docs = db.GetCollection("docs");
                docs.EnsureIndex("embedding", "$.Embedding", new VectorIndexOptions(2)).Should().BeFalse();
                docs.Insert(new BsonDocument { ["_id"] = 100, ["name"] = "d100", ["Embedding"] = new BsonVector(new[] { 100f, 1f }) });
                db.GetCollection("computed").Insert(new BsonDocument { ["_id"] = 100, ["Embedding"] = new BsonVector(new[] { 1f, 100f }) });
                Nearest(docs, 99f).Should().Contain(100);
            }

            using (var db = new LiteDatabase(connection))
            {
                AssertVectorIndexes(db);
                Nearest(db.GetCollection("docs"), 99f).Should().Contain(100);
                db.GetCollection("docs").Count(Query.EQ("name", "d7")).Should().Be(1);
                NearestComputed(db.GetCollection("computed"), 99f).Should().Contain(100);
            }
        }

        [Fact]
        public void Rebuild_of_a_prerelease_file_keeps_its_vector_indexes()
        {
            using var file = new TempFile();
            File.WriteAllBytes(file.Filename, Fixture("vectors.db"));

            using (var db = new LiteDatabase(file.Filename))
            {
                db.Rebuild();
                AssertVectorIndexes(db);
            }

            using var reopened = new LiteDatabase(file.Filename);
            AssertVectorIndexes(reopened);
            reopened.GetCollection("_rebuild_errors").Count().Should().Be(0);
        }

        [Fact]
        public void Auto_rebuild_reads_the_prerelease_vector_metadata()
        {
            using var file = new TempFile();
            var bytes = Fixture("vectors.db");
            bytes[HeaderPage.P_INVALID_DATAFILE_STATE] = 1; // request rebuild on open (FileReaderV8)
            File.WriteAllBytes(file.Filename, bytes);

            using var db = new LiteDatabase($"Filename={file.Filename};Auto-Rebuild=true");
            AssertVectorIndexes(db);
            db.GetCollection("_rebuild_errors").Count().Should().Be(0);
        }

        [Fact]
        public void Rebuild_reports_a_vector_index_without_metadata_instead_of_indexing_it_as_ordinary()
        {
            using var file = new TempFile();
            var bytes = Fixture("vectors.db");
            // Rename the vector section entry: the page still lists "embedding" as a vector index.
            var name = System.Text.Encoding.UTF8.GetBytes("embedding\0");
            var occurrences = Enumerable.Range(0, bytes.Length - name.Length).Where(i => bytes.Skip(i).Take(name.Length).SequenceEqual(name)).ToArray();
            occurrences.Should().HaveCount(2, "the index list entry and the vector section entry");
            bytes[occurrences[1] + name.Length - 2] = (byte)'X';
            File.WriteAllBytes(file.Filename, bytes);

            Action open = () => new LiteDatabase(file.Filename).Dispose();
            var error = open.Should().Throw<LiteException>().Which;
            error.ErrorCode.Should().Be(LiteException.INVALID_DATAFILE_STATE);
            error.Message.Should().Contain("Collection 'docs'").And.Contain("'embedding' has no vector metadata. ");

            using var db = new LiteDatabase($"Filename={file.Filename};Auto-Rebuild=true");
            db.GetCollection("$indexes").Find(Query.EQ("name", "embedding")).Should().BeEmpty();
            db.GetCollection("_rebuild_errors").FindAll().Select(x => x["message"].AsString)
                .Should().Contain(x => x.Contains("'embedding'"));
            db.GetCollection("docs").Count().Should().Be(40);
            NearestComputed(db.GetCollection("computed"), 7f).Should().Contain(7);
        }

        /// <summary>
        /// 5.0.21 dropped index "name" of vectors.db: its writer rewrote the index list without the
        /// entry and left the old bytes after it, where this version reads the vector section. Every
        /// open (read-only, default, auto-rebuild) threw a raw DecoderFallbackException and wrote no
        /// mark. A section that does not decode, or does not name exactly the page's vector indexes,
        /// is now ignored: the read-only open reads every document, a writable open reports the
        /// vector index without metadata, and the auto-rebuild drops only it.
        /// </summary>
        [Fact]
        public void Vector_file_whose_index_list_5_0_21_rewrote_opens_and_reports_the_vector_index()
        {
            using var file = new TempFile();
            var bytes = Fixture("vectors.db");
            DropIndexAs5021(bytes, "name");
            File.WriteAllBytes(file.Filename, bytes);

            using (var readOnly = new LiteDatabase($"Filename={file.Filename};readonly=true;legacy index scan=true"))
            {
                readOnly.GetCollection("docs").Count().Should().Be(40);
            }

            Action open = () => new LiteDatabase(file.Filename).Dispose();
            open.Should().Throw<LiteException>().Where(x => x.ErrorCode == LiteException.INVALID_DATAFILE_STATE)
                .WithMessage("*'embedding' has no vector metadata*");

            using var db = new LiteDatabase($"Filename={file.Filename};Auto-Rebuild=true");
            db.GetCollection("docs").Count().Should().Be(40);
            db.GetCollection("$indexes").Find(Query.EQ("collection", "docs")).Select(x => x["name"].AsString)
                .Should().BeEquivalentTo(new[] { "_id" }, "5.0.21 dropped \"name\", the rebuild drops \"embedding\"");
            db.GetCollection("_rebuild_errors").FindAll().Select(x => x["message"].AsString)
                .Should().Contain(x => x.Contains("'embedding'"));
            NearestComputed(db.GetCollection("computed"), 7f).Should().Contain(7);
        }

        /// <summary>
        /// A section that decodes but names more indexes than the page lists as vector indexes (here
        /// "embedding" and "other", beside a list without "other") is not this page's: "embedding"
        /// gets no metadata from it, and a writable open reports it.
        /// </summary>
        [Fact]
        public void Vector_section_naming_other_indexes_is_ignored()
        {
            using var file = new TempFile();
            var bytes = Fixture("vectors.db");
            WriteVectorSection(bytes, "embedding", "other");
            File.WriteAllBytes(file.Filename, bytes);

            using (var readOnly = new LiteDatabase($"Filename={file.Filename};readonly=true;legacy index scan=true"))
            {
                readOnly.GetCollection("docs").Count().Should().Be(40);
            }
            Action open = () => new LiteDatabase(file.Filename).Dispose();
            open.Should().Throw<LiteException>().Where(x => x.ErrorCode == LiteException.INVALID_DATAFILE_STATE)
                .WithMessage("*'embedding' has no vector metadata.*");
        }

        /// <summary>After the index list of the page listing "embedding", a section naming these indexes with its metadata.</summary>
        private static void WriteVectorSection(byte[] bytes, params string[] section)
        {
            for (var start = 0; start < bytes.Length; start += Constants.PAGE_SIZE)
            {
                if (bytes[start + BasePage.P_PAGE_TYPE] != (byte)PageType.Collection) continue;
                var page = new CollectionPage(new PageBuffer(bytes, start, 0));
                if (page.GetCollectionIndex("embedding") == null || page.GetCollectionIndex("name") == null) continue;
                var metadata = page.GetVectorIndexMetadata("embedding");
                var indexes = page.GetCollectionIndexes().ToArray();
                using var writer = new BufferWriter(new BufferSlice(bytes, start + CollectionPage.P_INDEXES, Constants.PAGE_SIZE - CollectionPage.P_INDEXES));
                writer.Write((byte)indexes.Length);
                foreach (var index in indexes) index.UpdateBuffer(writer);
                writer.Write((byte)section.Length);
                foreach (var name in section)
                {
                    writer.WriteCString(name);
                    metadata.UpdateBuffer(writer);
                }
                return;
            }
            throw new InvalidOperationException("no collection page lists embedding");
        }

        /// <summary>What 5.0.21's DropIndex writes: the index list without the entry, nothing after it.</summary>
        private static void DropIndexAs5021(byte[] bytes, string dropped)
        {
            for (var start = 0; start < bytes.Length; start += Constants.PAGE_SIZE)
            {
                if (bytes[start + BasePage.P_PAGE_TYPE] != (byte)PageType.Collection) continue;
                var page = new CollectionPage(new PageBuffer(bytes, start, 0));
                if (page.GetCollectionIndex(dropped) == null) continue;
                var kept = page.GetCollectionIndexes().Where(x => x.Name != dropped).ToArray();
                using var writer = new BufferWriter(new BufferSlice(bytes, start + CollectionPage.P_INDEXES, Constants.PAGE_SIZE - CollectionPage.P_INDEXES));
                writer.Write((byte)kept.Length);
                foreach (var index in kept) index.UpdateBuffer(writer);
                return;
            }
            throw new InvalidOperationException("no collection page lists " + dropped);
        }

        /// <summary>
        /// vector-salvage.db (prerelease.114): collection "vectors" with {_id: 1..3, a, b: "u-i",
        /// v: [i, 1]} and vector index "vv" on IIF(SUBSTRING(COALESCE($.b, 'x'), 1, 2) = '-0', $.v, $.v);
        /// then the length of "b" of document 2 was damaged. Its readable part makes the vector
        /// expression throw; it is reported instead of failing the rebuild.
        /// </summary>
        [Fact]
        public void Rebuild_reports_a_damaged_document_whose_vector_value_cannot_be_computed()
        {
            using var file = new TempFile();
            File.WriteAllBytes(file.Filename, Fixture("vector-salvage.db"));

            using var db = new LiteDatabase($"Filename={file.Filename};Auto-Rebuild=true");
            db.GetCollection("vectors").FindAll().Select(x => x["_id"].AsInt32).Should().BeEquivalentTo(new[] { 1, 3 });
            db.GetCollection("$indexes").Find(Query.EQ("name", "vv")).Single()["indexType"].AsInt32.Should().Be(1);
            db.GetCollection("_rebuild_errors").FindAll().Select(x => x["message"].AsString)
                .Should().Contain(x => x.Contains("damaged document 2 was not kept") && x.Contains("vector index 'vv'"));
        }

        private static void AssertVectorIndexes(LiteDatabase db)
        {
            var indexes = db.GetCollection("$indexes").FindAll().ToDictionary(x => x["collection"].AsString + "." + x["name"].AsString);
            indexes["docs.embedding"]["indexType"].AsInt32.Should().Be(1);
            indexes["computed.coalesced"]["indexType"].AsInt32.Should().Be(1);
            indexes["docs.name"]["indexType"].AsInt32.Should().Be(0);

            var docs = db.GetCollection("docs");
            var query = docs.Query().TopKNear(BsonExpression.Create("$.Embedding"), new[] { 20f, 1f }, Candidates);
            query.GetPlan()["index"]["name"].AsString.Should().Be("embedding");
            query.ToArray().Select(x => x["_id"].AsInt32).Should().Contain(20);
            docs.Count().Should().BeGreaterOrEqualTo(40);

            NearestComputed(db.GetCollection("computed"), 7f).Should().Contain(7);
        }

        // The vector index is approximate (and "coalesced" maps every fifth document to [0, 0]):
        // assert that the expected document is among the nearest few, not that it is first.
        private const int Candidates = 5;

        private static int[] Nearest(ILiteCollection<BsonDocument> docs, float x) =>
            docs.Query().TopKNear(BsonExpression.Create("$.Embedding"), new[] { x, 1f }, Candidates).ToArray().Select(d => d["_id"].AsInt32).ToArray();

        private static int[] NearestComputed(ILiteCollection<BsonDocument> computed, float y)
        {
            var query = computed.Query().TopKNear(BsonExpression.Create("COALESCE($.Embedding, [0, 0])"), new[] { 1f, y }, Candidates);
            query.GetPlan()["index"]["name"].AsString.Should().Be("coalesced");
            return query.ToArray().Select(d => d["_id"].AsInt32).ToArray();
        }

        private static byte[] Fixture(string name)
        {
            using var resource = typeof(PrereleaseVectorFile_Tests).Assembly.GetManifestResourceStream(
                "LiteDB.Tests.Resources.Vectors_6_0_0_prerelease_114.zip");
            using var zip = new ZipArchive(resource, ZipArchiveMode.Read);
            using var entry = zip.GetEntry(name).Open();
            using var bytes = new MemoryStream();
            entry.CopyTo(bytes);
            return bytes.ToArray();
        }
    }
}
