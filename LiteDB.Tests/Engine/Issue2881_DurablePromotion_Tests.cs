using System;
using System.IO;
using System.Linq;
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

        [Theory]
        [InlineData(null)]
        [InlineData("password")]
        public void Failed_durable_flush_aborts_promotion_without_committing_vectors(string password)
        {
            using var file = new TempFile();
            using var stream = new TrackingFileStream(file.Filename);
            using var log = new MemoryStream();
            var settings = new EngineSettings { DataStream = stream, LogStream = log, Password = password };
            using (var engine = new LiteEngine(settings))
            using (var db = new LiteDatabase(engine, disposeOnClose: false))
            {
                var docs = db.GetCollection("docs");
                docs.Insert(new BsonDocument { ["_id"] = 1 });
                db.Checkpoint();
                var writesBeforeFlushFailure = 0;
                engine.SimulateDiskWriteFail = page =>
                {
                    if (!stream.FlushFailed) writesBeforeFlushFailure++;
                };
                stream.HeaderOffset = password == null ? 0 : Constants.PAGE_SIZE;
                stream.FailDurableFlush = true;
                Action insert = () => docs.Insert(new BsonDocument
                {
                    ["_id"] = 2, ["vector"] = new BsonVector(new[] { 1f, 0f })
                });
                insert.Should().Throw<IOException>().WithMessage("Injected durable flush failure");
                writesBeforeFlushFailure.Should().Be(0, "vector pages must not reach the WAL before promotion is durable");
            }
            using var reopenedEngine = new LiteEngine(settings);
            using var reopened = new LiteDatabase(reopenedEngine, disposeOnClose: false);
            reopened.GetCollection("docs").FindAll().Select(x => x["_id"].AsInt32).Should().Equal(1);
        }

        private sealed class TrackingFileStream : FileStream
        {
            internal int DurableFlushes;
            internal bool FailDurableFlush;
            internal bool FlushFailed;
            internal long HeaderOffset;
            private bool _headerWritten;

            internal TrackingFileStream(string filename)
                : base(filename, FileMode.Create, FileAccess.ReadWrite, FileShare.ReadWrite) { }

            public override void Write(byte[] buffer, int offset, int count)
            {
                if (FailDurableFlush && Position == HeaderOffset && count == Constants.PAGE_SIZE) _headerWritten = true;
                base.Write(buffer, offset, count);
            }

            public override void Flush(bool flushToDisk)
            {
                if (flushToDisk && FailDurableFlush && _headerWritten)
                {
                    FailDurableFlush = false;
                    FlushFailed = true;
                    throw new IOException("Injected durable flush failure");
                }
                if (flushToDisk) DurableFlushes++;
                base.Flush(flushToDisk);
            }
        }
    }
}
