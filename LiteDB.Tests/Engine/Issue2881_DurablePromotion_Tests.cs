using System;
using System.IO;
using FluentAssertions;
using LiteDB.Engine;
using LiteDB.Internals;
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
        [InlineData(null, false)]
        [InlineData("password", false)]
        [InlineData(null, true)]
        [InlineData("password", true)]
        public void Writable_open_durably_publishes_checksums_before_accepting_transactions(string password, bool failFlush)
        {
            using var file = new TempFile();
            using var stream = new TrackingFileStream(file.Filename);
            using var log = new MemoryStream();
            var settings = new EngineSettings { DataStream = stream, LogStream = log, Password = password };
            using (var engine = new LiteEngine(settings))
            using (var db = new LiteDatabase(engine, disposeOnClose: false))
            {
                db.GetCollection("docs").Insert(new BsonDocument { ["_id"] = 1 });
                db.Checkpoint();
            }
            ChecksumTestFiles.MakeLegacy(stream, log, password);
            var before = stream.DurableFlushes;
            stream.HeaderOffset = password == null ? 0 : Constants.PAGE_SIZE;
            stream.FailDurableFlush = failFlush;
            if (failFlush)
            {
                Action open = () => { using var engine = new LiteEngine(settings); };
                open.Should().Throw<IOException>().WithMessage("Injected durable flush failure");
                stream.FlushFailed.Should().BeTrue();
                using var factory = new StreamFactory(log, password);
                using var journalStream = factory.GetStream(false, false);
                HeaderJournal.Read(journalStream).Should().NotBeNull("failed publication retains legacy redo and a header recovery copy");
            }
            using var reopenedEngine = new LiteEngine(settings);
            using var reopened = new LiteDatabase(reopenedEngine, disposeOnClose: false);
            if (!failFlush) stream.DurableFlushes.Should().BeGreaterThanOrEqualTo(before + 2);
            reopened.GetCollection("docs").Count().Should().Be(1);
            reopened.GetCollection("docs").Insert(new BsonDocument { ["_id"] = 2, ["vector"] = new BsonVector(new[] { 1f, 0f }) });
            reopened.Checkpoint();
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
