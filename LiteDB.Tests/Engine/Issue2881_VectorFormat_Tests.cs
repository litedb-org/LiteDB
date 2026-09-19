using System;
using System.IO;
using FluentAssertions;
using LiteDB.Engine;
using LiteDB.Vector;
using Xunit;

namespace LiteDB.Tests.Engine
{
    public class Issue2881_VectorFormat_Tests
    {
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void Vector_files_require_a_version_distinguishable_from_legacy_v8(bool index)
        {
            using var stream = new MemoryStream();
            using (var db = new LiteDatabase(stream))
            {
                var docs = db.GetCollection("docs");
                docs.Insert(new BsonDocument { ["_id"] = 1, ["Embedding"] = new BsonVector(new[] { 1f, 0f }) });
                if (index)
                {
                    docs.EnsureIndex("embedding_idx", "$.Embedding", new VectorIndexOptions(2));
                }
                db.Checkpoint();
                stream.ToArray()[HeaderPage.P_FILE_VERSION].Should().Be(9);
            }
        }

        [Fact]
        public void Unknown_file_version_reports_a_dedicated_error_without_changing_the_file()
        {
            using var file = new TempFile();
            using (var db = new LiteDatabase(file.Filename)) db.GetCollection("docs").Insert(new BsonDocument { ["_id"] = 1 });
            var bytes = File.ReadAllBytes(file.Filename);
            bytes[HeaderPage.P_FILE_VERSION] = 10;
            File.WriteAllBytes(file.Filename, bytes);
            Action open = () =>
            {
                using var db = new LiteDatabase(file.Filename);
                db.GetCollection("docs").Count();
            };
            var error = open.Should().Throw<LiteException>().Which;
            error.ErrorCode.Should().Be(LiteException.UNSUPPORTED_FILE_VERSION);
            error.Message.Should().Contain("version 10").And.Contain("versions 8 and 9");
            File.ReadAllBytes(file.Filename).Should().Equal(bytes);
        }

        [Fact]
        public void Rebuild_preserves_vector_documents_metadata_and_version()
        {
            using var file = new TempFile();
            try
            {
                using (var db = new LiteDatabase(file.Filename))
                {
                    var docs = db.GetCollection("docs");
                    docs.Insert(new BsonDocument { ["_id"] = 1, ["Embedding"] = new BsonVector(new[] { 1f, 0f }) });
                    docs.EnsureIndex("embedding_idx", "$.Embedding", new VectorIndexOptions(2));
                    db.Rebuild();
                    docs.Query().TopKNear("Embedding", new[] { 1f, 0f }, 1).ToArray().Should().ContainSingle();
                }
                File.ReadAllBytes(file.Filename)[HeaderPage.P_FILE_VERSION].Should().Be(9);
                using var reopened = new LiteDatabase(file.Filename);
                reopened.GetCollection("docs").FindById(1)["Embedding"].IsVector.Should().BeTrue();
                var query = reopened.GetCollection("docs").Query().TopKNear("Embedding", new[] { 1f, 0f }, 1);
                query.GetPlan()["index"]["name"].AsString.Should().Be("embedding_idx");
                query.GetPlan()["index"]["mode"].AsString.Should().Be("VECTOR INDEX SEARCH");
                query.ToArray().Should().ContainSingle();
            }
            finally
            {
                File.Delete(Path.Combine(Path.GetDirectoryName(file.Filename), Path.GetFileNameWithoutExtension(file.Filename) + "-backup.db"));
            }
        }
    }
}
