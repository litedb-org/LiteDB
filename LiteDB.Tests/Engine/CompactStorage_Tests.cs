using System;
using System.IO;
using System.Linq;
using FluentAssertions;
using LiteDB.Engine;
using LiteDB.Vector;
using Xunit;

namespace LiteDB.Tests.Engine
{
    public class CompactStorage_Tests
    {
        internal static BsonDocument Document(int id)
        {
            var doc = new BsonDocument { ["_id"] = id };
            for (var i = 0; i < 15; i++) doc["RepeatedPropertyName" + i] = id + i;
            return doc;
        }

        [Theory]
        [InlineData(null)]
        [InlineData("password")]
        public void Mixed_documents_survive_reopen_indexes_rename_rebuild_and_downgrade(string password)
        {
            using var file = new TempFile();
            var connection = new ConnectionString { Filename = file.Filename, Password = password };
            using (var db = new LiteDatabase(connection)) db.GetCollection("docs").Insert(Document(1));
            var original = File.ReadAllBytes(file.Filename);
            connection.CompactStorage = true;
            connection.ReadOnly = true;
            using (var db = new LiteDatabase(connection)) Assert.NotNull(db.GetCollection("docs").FindById(1));
            File.ReadAllBytes(file.Filename).Should().Equal(original);
            connection.ReadOnly = false;
            using (var db = new LiteDatabase(connection)) { db.GetCollectionNames().ToArray(); }
            File.ReadAllBytes(file.Filename).Should().Equal(original);
            using (var db = new LiteDatabase(connection))
            {
                var docs = db.GetCollection("docs");
                docs.Insert(Enumerable.Range(2, 50).Select(Document));
                docs.EnsureIndex("property", "$.RepeatedPropertyName0");
                docs.Find("$.RepeatedPropertyName0 >= 40").Count().Should().Be(12);
                docs.Update(Document(1)).Should().BeTrue();
                docs.Delete(51).Should().BeTrue();
                db.RenameCollection("docs", "renamed").Should().BeTrue();
                db.Rebuild(new RebuildOptions { Password = password, CompactStorage = true });
                db.GetCollection("renamed").FindAll().Count().Should().Be(50);
                db.Rebuild(new RebuildOptions { Password = password, CompactStorage = false });
            }
            connection.CompactStorage = false;
            using (var db = new LiteDatabase(connection))
            {
                BsonSerializer.Serialize(db.GetCollection("renamed").FindById(1)).Should().Equal(BsonSerializer.Serialize(Document(1)));
                db.DropCollection("renamed").Should().BeTrue();
                db.GetCollection("new").Insert(Enumerable.Range(1, 50).Select(Document));
            }
            if (password == null) File.ReadAllBytes(file.Filename)[HeaderPage.P_FILE_VERSION].Should().Be(8);
        }

        [Fact]
        public void Rollback_discards_schema_references_but_retains_durable_promotion()
        {
            using var stream = new MemoryStream();
            using var log = new MemoryStream();
            var settings = new EngineSettings { DataStream = stream, LogStream = log, CompactStorage = true, TransactionPageLimit = 4 };
            using (var db = new LiteDatabase(new LiteEngine(settings)))
            {
                db.CheckpointSize = 0;
                db.GetCollection("docs").Insert(new BsonDocument { ["_id"] = 1 });
                db.Checkpoint();
                db.BeginTrans();
                db.GetCollection("docs").Insert(Enumerable.Range(2, 200).Select(Document));
                stream.ToArray()[59].Should().Be(10);
                db.Rollback();
                db.GetCollection("docs").FindAll().Select(d => d["_id"].AsInt32).Should().Equal(1);
                db.GetCollection("docs").Insert(Enumerable.Range(2, 100).Select(Document));
            }
            using (var db = new LiteDatabase(new LiteEngine(settings)))
            {
                db.GetCollection("docs").FindAll().Count().Should().Be(101);
                db.Checkpoint();
                stream.ToArray()[59].Should().Be(10);
                db.DropCollection("docs");
                db.GetCollection("replacement").Insert(Enumerable.Range(1, 100).Select(Document));
                BsonSerializer.Serialize(db.GetCollection("replacement").FindById(10)).Should().Equal(BsonSerializer.Serialize(Document(10)));
            }
        }

        [Fact]
        public void Compact_storage_preserves_vector_queries_and_v9_downgrade_floor()
        {
            using var file = new TempFile();
            var docs = Enumerable.Range(1, 20).Select(i =>
            {
                var doc = Document(i);
                doc["Embedding"] = new BsonVector(new[] { (float)i, 1f });
                return doc;
            }).ToArray();
            using (var db = new LiteDatabase(new ConnectionString { Filename = file.Filename, CompactStorage = true }))
            {
                db.GetCollection("docs").Insert(docs);
                db.GetCollection("docs").EnsureIndex("vector", "$.Embedding", new VectorIndexOptions(2));
                db.GetCollection("docs").Query().TopKNear("Embedding", new[] { 1f, 1f }, 1).ToArray().Length.Should().Be(1);
                db.Rebuild(new RebuildOptions { CompactStorage = false });
                db.GetCollection("docs").FindById(1)["Embedding"].IsVector.Should().BeTrue();
            }
            // File.ReadAllBytes cannot share the live engine's file handle on Windows.
            File.ReadAllBytes(file.Filename)[59].Should().Be(9);
        }

        [Fact]
        public void Connection_option_round_trips()
        {
            var parsed = new ConnectionString("Filename=:memory:;Compact Storage=true");
            parsed.CompactStorage.Should().BeTrue();
            new ConnectionString(parsed.ToString()).CompactStorage.Should().BeTrue();
            new ConnectionString().CompactStorage.Should().BeFalse();
        }
    }
}
