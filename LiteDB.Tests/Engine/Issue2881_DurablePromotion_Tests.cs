using System.IO;
using FluentAssertions;
using LiteDB.Engine;
using Xunit;

namespace LiteDB.Tests.Engine
{
    public class Issue2881_DurablePromotion_Tests
    {
        [Fact]
        public void Encrypted_stream_forwards_durable_flush_to_the_file()
        {
            using var file = new TempFile();
            using var stream = new TrackingFileStream(file.Filename);
            using var encrypted = new AesStream("password", stream);
            var before = stream.DurableFlushes;
            encrypted.FlushToDisk();
            stream.DurableFlushes.Should().Be(before + 1);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("password")]
        public void Promotion_durably_flushes_through_caller_stream_wrappers_before_commit(string password)
        {
            using var file = new TempFile();
            using var stream = new TrackingFileStream(file.Filename);
            using var engine = new LiteEngine(new EngineSettings { DataStream = stream, Password = password });
            using var db = new LiteDatabase(engine, disposeOnClose: false);
            var docs = db.GetCollection("docs");
            docs.Insert(new BsonDocument { ["_id"] = 1 });
            db.Checkpoint();
            db.BeginTrans();
            var before = stream.DurableFlushes;
            docs.Insert(new BsonDocument { ["_id"] = 2, ["vector"] = new BsonVector(new[] { 1f, 0f }) });
            stream.DurableFlushes.Should().BeGreaterThan(before, "promotion must reach FileStream.Flush(true) before vector commit");
            db.Rollback();
        }

        private sealed class TrackingFileStream : FileStream
        {
            internal int DurableFlushes;

            internal TrackingFileStream(string filename)
                : base(filename, FileMode.Create, FileAccess.ReadWrite, FileShare.ReadWrite) { }

            public override void Flush(bool flushToDisk)
            {
                if (flushToDisk) DurableFlushes++;
                base.Flush(flushToDisk);
            }
        }
    }
}
