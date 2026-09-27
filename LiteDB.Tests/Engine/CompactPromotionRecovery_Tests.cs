using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using FluentAssertions;
using LiteDB.Engine;
using LiteDB.Internals;
using Xunit;

namespace LiteDB.Tests.Engine
{
    public class CompactPromotionRecovery_Tests
    {
        [Theory]
        [InlineData(null)]
        [InlineData("secret")]
        public void Torn_v11_to_v12_publication_preserves_committed_wal_and_can_resume(string password)
        {
            using var data = new CapturingStream();
            using var log = new MemoryStream();
            var settings = new EngineSettings
            {
                DataStream = data, LogStream = log, Password = password,
                CompactStorage = CompactStorageMode.Compact
            };
            using var engine = new LiteEngine(settings);
            using var db = new LiteDatabase(engine, disposeOnClose: false);
            db.CheckpointSize = 0;
            var docs = db.GetCollection("docs");
            docs.EnsureIndex("RepeatedPropertyName0");
            docs.Insert(CompactStorage_Tests.Document(1));
            db.Checkpoint();
            // Leave acknowledged BSON in the WAL before the first compact write.
            db.GetCollection("other").Insert(new BsonDocument { ["_id"] = 1, ["payload"] = "acknowledged" });
            data.Log = log;
            data.Capture = true;
            docs.Insert(CompactStorage_Tests.Document(2));
            data.Capture = false;
            data.Images.Should().ContainSingle("promotion only overwrites the persisted header");
            var write = data.Images.Single();
            foreach (var prefix in new[] { 0, 1, 16, 64, 128, 512, 4096, Constants.PAGE_SIZE })
            foreach (var readOnly in new[] { true, false })
            {
                var torn = (byte[])write.Before.Clone();
                Buffer.BlockCopy(write.Bytes, 0, torn, write.Position, prefix);
                using var recoveredData = ChecksumTestFiles.Copy(torn);
                using var recoveredLog = ChecksumTestFiles.Copy(write.Wal);
                var recoveredSettings = new EngineSettings
                {
                    DataStream = recoveredData, LogStream = recoveredLog, Password = password,
                    ReadOnly = readOnly, CompactStorage = CompactStorageMode.Compact
                };
                using (var recovered = new LiteDatabase(new LiteEngine(recoveredSettings)))
                {
                    Verify(recovered, 1);
                    recovered.GetCollection("other").FindById(1)["payload"].AsString.Should().Be("acknowledged");
                    if (!readOnly)
                    {
                        recovered.GetCollection("docs").Insert(CompactStorage_Tests.Document(2));
                        recovered.Checkpoint();
                    }
                }
                if (readOnly)
                {
                    recoveredData.ToArray().Should().Equal(torn);
                    recoveredLog.ToArray().Should().Equal(write.Wal);
                }
                else
                {
                    using var reopened = new LiteDatabase(new LiteEngine(recoveredSettings));
                    Verify(reopened, 2);
                }
            }
        }

        private static void Verify(LiteDatabase db, int count)
        {
            var docs = db.GetCollection("docs");
            docs.Count().Should().Be(count);
            for (var id = 1; id <= count; id++)
            {
                var expected = CompactStorage_Tests.Document(id);
                BsonSerializer.Serialize(docs.FindById(id)).Should().Equal(BsonSerializer.Serialize(expected));
                var indexed = docs.Find(Query.EQ("RepeatedPropertyName0", expected["RepeatedPropertyName0"])).Single();
                BsonSerializer.Serialize(indexed).Should().Equal(BsonSerializer.Serialize(expected));
            }
        }

        private sealed class CapturingStream : MemoryStream
        {
            internal bool Capture;
            internal MemoryStream Log;
            internal readonly List<(byte[] Before, byte[] Wal, byte[] Bytes, int Position)> Images =
                new List<(byte[], byte[], byte[], int)>();

            public override void Write(byte[] buffer, int offset, int count)
            {
                if (Capture)
                    Images.Add((ToArray(), Log.ToArray(), buffer.Skip(offset).Take(count).ToArray(), checked((int)Position)));
                base.Write(buffer, offset, count);
            }
        }
    }
}
