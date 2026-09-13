using System;
using System.IO;
using System.Threading.Tasks;
using FluentAssertions;
using LiteDB.Engine;
using LiteDB.Vector;
using Xunit;

namespace LiteDB.Tests.Engine
{
    public class Issue2881_VectorPromotion_Tests
    {
        [Theory]
        [InlineData(false, false)]
        [InlineData(true, false)]
        [InlineData(false, true)]
        [InlineData(true, true)]
        public void Ordinary_v8_files_open_without_rebuild(bool readOnly, bool upgrade)
        {
            using var file = new TempFile();
            using (var db = new LiteDatabase(file.Filename)) db.GetCollection("docs").Insert(new BsonDocument { ["_id"] = 1 });
            var original = ReadDataFile(file.Filename);
            original[59].Should().Be(8);
            using (var db = new LiteDatabase(new ConnectionString { Filename = file.Filename, ReadOnly = readOnly, Upgrade = upgrade }))
            {
                db.GetCollection("docs").Count().Should().Be(1);
            }
            ReadDataFile(file.Filename).Should().Equal(original);
            File.Exists(Path.ChangeExtension(file.Filename, null) + "-backup.db").Should().BeFalse();
        }

        [Theory]
        [InlineData("insert")]
        [InlineData("update")]
        [InlineData("upsert")]
        [InlineData("index")]
        public void First_vector_write_promotes_only_the_header_before_commit(string operation)
        {
            using var file = new TempFile();
            using var db = new LiteDatabase(file.Filename);
            var docs = db.GetCollection("docs");
            docs.Insert(new BsonDocument { ["_id"] = 1, ["Embedding"] = new BsonArray { 1, 0 } });
            docs.EnsureIndex("ordinary", "$.Embedding");
            db.Checkpoint();
            var original = ReadDataFile(file.Filename);
            original[59].Should().Be(8);
            db.CheckpointSize = 0;
            db.BeginTrans();
            var document = new BsonDocument
            {
                ["_id"] = operation == "insert" ? 2 : 1,
                ["nested"] = new BsonArray { new BsonDocument { ["vector"] = new BsonVector(new[] { 1f, 0f }) } }
            };
            if (operation == "index") docs.EnsureIndex("vector", "$.Embedding", new VectorIndexOptions(2));
            else if (operation == "update") docs.Update(document);
            else if (operation == "upsert") docs.Upsert(document);
            else docs.Insert(document);

            original[59] = 9;
            ReadDataFile(file.Filename).Should().Equal(original, "promotion must precede vector commit and preserve every other data byte");
            db.Rollback();
            db.Checkpoint();
            ReadDataFile(file.Filename)[59].Should().Be(9, "rollback must not undo the compatibility boundary");
        }

        [Fact]
        public void Replaying_older_wal_headers_cannot_downgrade_a_promoted_file()
        {
            using var data = new MemoryStream();
            using var log = new MemoryStream();
            using var db = new LiteDatabase(data, logStream: log);
            db.CheckpointSize = 0;
            db.GetCollection("docs").Insert(new BsonDocument { ["_id"] = 1 });
            data.ToArray()[59].Should().Be(8);
            db.BeginTrans();
            db.GetCollection("docs").Insert(new BsonDocument { ["_id"] = 2, ["vector"] = new BsonVector(new[] { 1f, 0f }) });
            using var replayData = Copy(data);
            using var replayLog = Copy(log);
            db.Rollback();
            using (var reopened = new LiteDatabase(replayData, logStream: replayLog))
            {
                reopened.GetCollection("docs").Count().Should().Be(1);
                reopened.Checkpoint();
                replayData.ToArray()[59].Should().Be(9);
                reopened.GetCollection("ordinary").Insert(new BsonDocument { ["_id"] = 1 });
                reopened.Checkpoint();
                replayData.ToArray()[59].Should().Be(9);
            }
        }

        [Theory]
        [InlineData(null)]
        [InlineData("promotion-password")]
        public void Shared_connections_observe_the_promoted_version(string password)
        {
            using var file = new TempFile();
            var connection = new ConnectionString { Filename = file.Filename, Password = password, Connection = ConnectionType.Shared };
            using var first = new LiteDatabase(connection);
            using var second = new LiteDatabase(connection);
            first.GetCollection("docs").Insert(new BsonDocument { ["_id"] = 1 });
            ReadVersion(file.Filename, password).Should().Be(8);
            second.GetCollection("docs").Insert(new BsonDocument { ["_id"] = 2, ["vector"] = new BsonVector(new[] { 1f, 0f }) });
            first.GetCollection("docs").FindById(2)["vector"].IsVector.Should().BeTrue();
            first.Checkpoint();
            ReadVersion(file.Filename, password).Should().Be(9);
        }

        [Fact]
        public async Task Concurrent_ordinary_and_vector_commits_preserve_promotion()
        {
            using var file = new TempFile();
            using var db = new LiteDatabase(file.Filename);
            await Task.WhenAll(Task.Run(() => db.GetCollection("ordinary").Insert(new BsonDocument { ["_id"] = 1 })),
                Task.Run(() => db.GetCollection("vectors").Insert(new BsonDocument { ["_id"] = 1, ["v"] = new BsonVector(new[] { 1f, 0f }) })));
            db.Checkpoint();
            ReadVersion(file.Filename, null).Should().Be(9);
            db.GetCollection("ordinary").Count().Should().Be(1);
            db.GetCollection("vectors").Count().Should().Be(1);
        }

        private static byte[] ReadDataFile(string filename)
        {
            // Inspect the flushed bytes while the database still owns its writable handle.
            using var stream = new FileStream(filename, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var copy = new MemoryStream();
            stream.CopyTo(copy);
            return copy.ToArray();
        }

        private static byte ReadVersion(string filename, string password)
        {
            using var factory = new FileStreamFactory(filename, password, true, false);
            using var stream = factory.GetStream(false, true);
            var header = new byte[Constants.PAGE_SIZE];
            stream.Read(header, 0, header.Length);
            return header[59];
        }

        private static MemoryStream Copy(MemoryStream source)
        {
            var copy = new MemoryStream();
            var bytes = source.ToArray();
            copy.Write(bytes, 0, bytes.Length);
            copy.Position = 0;
            return copy;
        }
    }
}
