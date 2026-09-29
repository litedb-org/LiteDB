using System.IO;
using System.Linq;
using FluentAssertions;
using LiteDB.Engine;
using Xunit;

namespace LiteDB.Tests.Regressions
{
    /// <summary>
    /// A caller stream's Flush() is the only sync the engine can ask of it, and a custom stream may
    /// sync there (the migration recovery harness's stream calls FileStream.Flush(true)). The engine
    /// flushes a caller stream once per WAL batch and per checkpoint sync, as 5.0.21 did, not after
    /// every page: flushing each write made one sync per page (14,370 syncs instead of 3,348 for the
    /// harness's crash-at-commit case, which then passed its time limit on Windows).
    /// </summary>
    [Trait("Category", "IoSafety")]
    public class CallerStreamFlushCost_Tests
    {
        [Theory]
        [InlineData(null)]
        [InlineData("secret")]
        public void A_commit_and_a_checkpoint_flush_a_caller_stream_a_few_times_not_per_page(string password)
        {
            using var data = new CountingStream();
            using var log = new CountingStream();
            using var engine = new LiteEngine(new EngineSettings { DataStream = data, LogStream = log, Password = password });
            using var db = new LiteDatabase(engine, disposeOnClose: false);
            db.CheckpointSize = 0;
            var rows = db.GetCollection("rows");
            // The first commit runs the once-per-engine barriers.
            rows.Insert(Row(0));

            var (logWrites, logFlushes) = (log.Writes, log.Flushes);
            rows.Insert(Enumerable.Range(1, 200).Select(Row));
            (log.Writes - logWrites).Should().BeGreaterThan(30, "the commit wrote a frame per page");
            (log.Flushes - logFlushes).Should().BeLessOrEqualTo(2, "a WAL batch flushes the log once, not per frame");

            var (dataWrites, dataFlushes) = (data.Writes, data.Flushes);
            logFlushes = log.Flushes;
            db.Checkpoint();
            (data.Writes - dataWrites).Should().BeGreaterThan(30, "the checkpoint wrote every page");
            (data.Flushes - dataFlushes).Should().BeLessOrEqualTo(6, "a checkpoint syncs the data file a few times, not per page");
            (log.Flushes - logFlushes).Should().BeLessOrEqualTo(6, "a checkpoint syncs the log a few times, not per page");

            rows.Count().Should().Be(201);
        }

        private static BsonDocument Row(int id) => new BsonDocument { ["_id"] = id, ["payload"] = new string('p', 1500) };

        /// <summary>A caller stream of the caller's own type (not a MemoryStream) that counts writes and flushes.</summary>
        private sealed class CountingStream : Stream
        {
            private readonly MemoryStream _inner = new MemoryStream();

            internal int Writes, Flushes;

            public override bool CanRead => true;
            public override bool CanSeek => true;
            public override bool CanWrite => true;
            public override long Length => _inner.Length;
            public override long Position { get => _inner.Position; set => _inner.Position = value; }
            public override void Flush() => Flushes++;
            public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);
            public override long Seek(long offset, SeekOrigin origin) => _inner.Seek(offset, origin);
            public override void SetLength(long value) => _inner.SetLength(value);

            public override void Write(byte[] buffer, int offset, int count)
            {
                Writes++;
                _inner.Write(buffer, offset, count);
            }

            protected override void Dispose(bool disposing)
            {
                if (disposing) _inner.Dispose();
                base.Dispose(disposing);
            }
        }
    }
}
