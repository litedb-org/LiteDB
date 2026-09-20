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
        public void Migration_durably_flushes_through_caller_stream_wrappers(string password)
        {
            using var file = new TempFile();
            CreateLegacy(file.Filename, password);
            using var stream = new TrackingFileStream(file.Filename);
            using var log = new MemoryStream();
            using var engine = new LiteEngine(new EngineSettings { DataStream = stream, LogStream = log, Password = password });
            stream.DurableFlushes.Should().BeGreaterThan(0);
            using var db = new LiteDatabase(engine, disposeOnClose: false);
            db.GetCollection("docs").Count().Should().Be(1);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("password")]
        public void Failed_durable_flush_aborts_migration_before_wal_writes(string password)
        {
            using var file = new TempFile();
            CreateLegacy(file.Filename, password);
            using var stream = new TrackingFileStream(file.Filename);
            using var log = new MemoryStream();
            var settings = new EngineSettings { DataStream = stream, LogStream = log, Password = password };
            stream.HeaderOffset = password == null ? 0 : Constants.PAGE_SIZE;
            stream.FailDurableFlush = true;
            Action open = () => { using var engine = new LiteEngine(settings); };
            open.Should().Throw<IOException>().WithMessage("Injected durable flush failure");
            stream.FlushFailed.Should().BeTrue();
            log.Length.Should().Be(0, "migration pages must not reach the WAL before promotion is durable");
            using var reopenedEngine = new LiteEngine(settings);
            using var reopened = new LiteDatabase(reopenedEngine, disposeOnClose: false);
            reopened.GetCollection("docs").FindAll().Select(x => x["_id"].AsInt32).Should().Equal(1);
        }

        private static void CreateLegacy(string filename, string password)
        {
            using (var db = IndexMigration_Tests.Open(filename, password))
                db.GetCollection("docs").Insert(new BsonDocument { ["_id"] = 1 });
            IndexMigration_Tests.RewriteHeaders(filename, password, header =>
            {
                header[HeaderPage.P_FILE_VERSION] = 8;
                header[EnginePragmas.P_INDEX_ORDER_VERSION] = 0;
                Array.Clear(header, EnginePragmas.P_COLLATION_STAMP, 4);
            });
        }

        private sealed class TrackingFileStream : FileStream
        {
            internal int DurableFlushes;
            internal bool FailDurableFlush;
            internal bool FlushFailed;
            internal long HeaderOffset;
            private bool _headerWritten;

            internal TrackingFileStream(string filename)
                : base(filename, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite) { }

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
