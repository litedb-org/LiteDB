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
        public void New_files_require_a_version_distinguishable_from_legacy_v8(bool index)
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

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void Legacy_version_requires_explicit_migration_without_modifying_source(bool readOnly)
        {
            using var file = new TempFile();
            using (var db = new LiteDatabase(file.Filename))
            {
                db.GetCollection("docs").Insert(new BsonDocument { ["_id"] = 1 });
            }
            var bytes = File.ReadAllBytes(file.Filename);
            // Ordinary v8 and v9 page layouts are identical; model a legacy ordinary file.
            bytes[HeaderPage.P_FILE_VERSION] = 8;
            File.WriteAllBytes(file.Filename, bytes);
            Action open = () =>
            {
                using var db = new LiteDatabase(new ConnectionString { Filename = file.Filename, ReadOnly = readOnly });
                db.GetCollection("docs").Count();
            };
            open.Should().Throw<LiteException>().Which.ErrorCode.Should().Be(LiteException.INVALID_DATABASE);
            File.ReadAllBytes(file.Filename).Should().Equal(bytes);
        }

        [Theory]
        [InlineData(null, false)]
        [InlineData(null, true)]
        [InlineData("migration-password", false)]
        [InlineData("migration-password", true)]
        public void Explicit_migration_preserves_legacy_data_and_vector_metadata(string password, bool vector)
        {
            using var file = new TempFile();
            var connection = new ConnectionString { Filename = file.Filename, Password = password };
            var backup = Path.Combine(Path.GetDirectoryName(file.Filename), Path.GetFileNameWithoutExtension(file.Filename) + "-backup.db");
            try
            {
                using (var db = new LiteDatabase(connection))
                {
                    var docs = db.GetCollection("docs");
                    docs.Insert(new BsonDocument { ["_id"] = 1, ["Embedding"] = new BsonVector(new[] { 1f, 0f }) });
                    if (vector) docs.EnsureIndex("embedding_idx", "$.Embedding", new VectorIndexOptions(2));
                }
                using (var factory = new FileStreamFactory(file.Filename, password, false, false))
                using (var stream = factory.GetStream(true, false))
                {
                    var header = new byte[Constants.PAGE_SIZE];
                    stream.Read(header, 0, header.Length);
                    header[HeaderPage.P_FILE_VERSION] = 8;
                    stream.Position = 0;
                    stream.Write(header, 0, header.Length);
                }
                var legacyBytes = File.ReadAllBytes(file.Filename);
                connection.Upgrade = true;
                using (var db = new LiteDatabase(connection))
                {
                    var docs = db.GetCollection("docs");
                    docs.FindById(1)["Embedding"].IsVector.Should().BeTrue();
                    if (vector)
                    {
                        var query = docs.Query().TopKNear("Embedding", new[] { 1f, 0f }, 1);
                        query.GetPlan()["index"]["mode"].AsString.Should().Be("VECTOR INDEX SEARCH");
                        query.ToArray().Should().ContainSingle();
                    }
                }
                File.ReadAllBytes(backup).Should().Equal(legacyBytes);
                using var reopened = new LiteDatabase(connection);
                reopened.GetCollection("docs").Count().Should().Be(1);
            }
            finally
            {
                File.Delete(backup);
            }
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
            }
            finally
            {
                File.Delete(Path.Combine(Path.GetDirectoryName(file.Filename), Path.GetFileNameWithoutExtension(file.Filename) + "-backup.db"));
            }
        }
    }
}
