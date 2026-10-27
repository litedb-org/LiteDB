using System;
using System.IO;
using System.Linq;
using FluentAssertions;
using LiteDB.Engine;
using LiteDB.Vector;
using Xunit;
using static LiteDB.Constants;

namespace LiteDB.Tests.Regressions
{
    public class VectorMetadataDamage_Tests
    {
        [Theory]
        [InlineData(null)]
        [InlineData("vector-secret")]
        public void Current_file_refuses_damaged_vector_queries_and_writes_without_mutation(string password)
        {
            using var file = new TempFile();
            var connection = Connection(file.Filename, password);

            using (var db = new LiteDatabase(connection))
            {
                var docs = db.GetCollection("docs");
                docs.EnsureIndex("embedding", "$.Embedding", new VectorIndexOptions(2, VectorDistanceMetric.Euclidean));
                docs.Insert(new BsonDocument { ["_id"] = 1, ["Embedding"] = new BsonVector(new[] { 100f, 0f }) });
                docs.Insert(new BsonDocument { ["_id"] = 2, ["Embedding"] = new BsonVector(new[] { 1f, 1f }) });

                docs.Query().TopKNear("Embedding", new[] { 1f, 0f }, 1).ToArray()
                    .Should().ContainSingle(x => x["_id"].AsInt32 == 2);
                db.Checkpoint();
            }

            CorruptVectorMetadata(file.Filename, password);
            var beforeData = File.ReadAllBytes(file.Filename);
            var logFile = FileHelper.GetLogFile(file.Filename);
            var beforeLog = File.Exists(logFile) ? File.ReadAllBytes(logFile) : null;

            using (var db = new LiteDatabase(connection))
            {
                Action query = () => db.GetCollection("docs")
                    .Query().TopKNear("Embedding", new[] { 1f, 0f }, 1).ToArray();
                var queryError = query.Should().Throw<LiteException>().Which;
                queryError.ErrorCode.Should().Be(LiteException.INVALID_DATAFILE_STATE);
                queryError.Message.Should().Contain("Rebuild").And.Contain("recreate");
            }

            File.ReadAllBytes(file.Filename).Should().Equal(beforeData);
            ReadBytes(logFile).Should().Equal(beforeLog ?? Array.Empty<byte>());

            using (var db = new LiteDatabase(connection))
            {
                Action write = () => db.GetCollection("docs").Insert(new BsonDocument
                {
                    ["_id"] = 3,
                    ["Embedding"] = new BsonVector(new[] { 1f, 2f })
                });
                var writeError = write.Should().Throw<LiteException>().Which;
                writeError.ErrorCode.Should().Be(LiteException.INVALID_DATAFILE_STATE);
                writeError.Message.Should().Contain("Rebuild").And.Contain("recreate");
            }

            File.ReadAllBytes(file.Filename).Should().Equal(beforeData);
            ReadBytes(logFile).Should().Equal(beforeLog ?? Array.Empty<byte>());
        }

        private static string Connection(string filename, string password)
        {
            return $"Filename={filename}" + (password == null ? "" : $";Password={password}");
        }

        private static byte[] ReadBytes(string filename)
        {
            return File.Exists(filename) ? File.ReadAllBytes(filename) : Array.Empty<byte>();
        }

        private static void CorruptVectorMetadata(string filename, string password)
        {
            var bytes = ReadPlainData(filename, password);

            for (var start = PAGE_SIZE; start < bytes.Length; start += PAGE_SIZE)
            {
                if (bytes[start + BasePage.P_PAGE_TYPE] != (byte)PageType.Collection) continue;

                var page = new CollectionPage(new PageBuffer(bytes, start, 0));
                if (page.GetCollectionIndex("embedding") == null) continue;

                using var reader = new BufferReader(new BufferSlice(bytes,
                    start + CollectionPage.P_INDEXES, PAGE_SIZE - CollectionPage.P_INDEXES));
                var count = reader.ReadByte();
                for (var i = 0; i < count; i++) _ = new CollectionIndex(reader);

                reader.ReadByte().Should().Be(1);
                var namePosition = start + CollectionPage.P_INDEXES + (int)reader.Position;
                bytes[namePosition].Should().NotBe(0xFF);
                bytes[namePosition] = 0xFF;

                var buffer = new PageBuffer(bytes, start, 0);
                PageChecksum.Write(buffer);
                PageChecksum.Validate(buffer, start);
                WritePlainData(filename, password, bytes);
                return;
            }

            throw new InvalidOperationException("The vector collection page was not found.");
        }

        private static byte[] ReadPlainData(string filename, string password)
        {
            if (password == null) return File.ReadAllBytes(filename);

            using var raw = new FileStream(filename, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var encrypted = new AesStream(password, raw, allowRecovery: false);
            var bytes = new byte[encrypted.Length];
            encrypted.ReadRequired(bytes, 0, bytes.Length);
            return bytes;
        }

        private static void WritePlainData(string filename, string password, byte[] bytes)
        {
            if (password == null)
            {
                File.WriteAllBytes(filename, bytes);
                return;
            }

            using var raw = new FileStream(filename, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
            using var encrypted = new AesStream(password, raw, allowRecovery: false);
            encrypted.Position = 0;
            encrypted.Write(bytes, 0, bytes.Length);
            encrypted.Flush();
        }
    }
}
