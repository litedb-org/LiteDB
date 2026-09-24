using System;
using System.IO;
using FluentAssertions;
using LiteDB.Engine;
using Xunit;
using static LiteDB.Constants;

namespace LiteDB.Tests.Internals
{
    public class StartupHeader_Tests
    {
        [Theory]
        [InlineData(8192)]
        [InlineData(17)]
        public void Open_consumes_one_complete_header_and_keeps_the_caller_stream_alive(int readSize)
        {
            using var file = Seed();
            var original = File.ReadAllBytes(file.Filename);
            using var data = new ObservedFile(file.Filename) { ReadSize = readSize };
            using var log = new MemoryStream();
            using (var engine = Open(data, log))
            using (var db = new LiteDatabase(engine, disposeOnClose: false))
            {
                data.HeaderBytes.Should().Be(PAGE_SIZE);
                db.GetCollection("rows").FindById(1)["value"].AsString.Should().Be("committed");
                db.GetCollection("other").FindById(9)["value"].AsInt32.Should().Be(123);
            }
            data.CanRead.Should().BeTrue();
            File.ReadAllBytes(file.Filename).Should().Equal(original);
            log.Length.Should().Be(0);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void Failed_header_reads_preserve_files_and_can_be_retried(bool eof)
        {
            using var file = Seed();
            var original = File.ReadAllBytes(file.Filename);
            using var data = new ObservedFile(file.Filename) { ReadSize = 17, FailAfter = 1234, Eof = eof };
            using var log = new MemoryStream();
            for (var attempt = 0; attempt < 2; attempt++)
            {
                data.HeaderBytes = 0;
                Action open = () => { using var engine = Open(data, log); };
                if (eof) open.Should().Throw<LiteException>().Which.ErrorCode.Should().Be(LiteException.INVALID_DATABASE);
                else open.Should().Throw<IOException>().WithMessage("header read failed");
                data.CanRead.Should().BeTrue();
                File.ReadAllBytes(file.Filename).Should().Equal(original);
                log.Length.Should().Be(0);
            }
            data.FailAfter = int.MaxValue;
            data.HeaderBytes = 0;
            using (var engine = Open(data, log))
            using (var db = new LiteDatabase(engine, disposeOnClose: false))
                db.GetCollection("rows").FindById(1)["value"].AsString.Should().Be("committed");
            File.ReadAllBytes(file.Filename).Should().Equal(original);
        }

        [Fact]
        public void Another_open_revalidates_the_header_and_does_not_reuse_earlier_bytes()
        {
            using var file = Seed();
            using var data = new ObservedFile(file.Filename);
            using var log = new MemoryStream();
            using (var engine = Open(data, log)) { }
            var original = File.ReadAllBytes(file.Filename);
            var corrupt = (byte[])original.Clone();
            corrupt[400] ^= 1;
            File.WriteAllBytes(file.Filename, corrupt);
            for (var attempt = 0; attempt < 2; attempt++)
            {
                Action open = () => { using var engine = Open(data, log); };
                open.Should().Throw<PageChecksumException>();
                File.ReadAllBytes(file.Filename).Should().Equal(corrupt);
                log.Length.Should().Be(0);
            }
            File.WriteAllBytes(file.Filename, original);
            using var restoredEngine = Open(data, log);
            using var db = new LiteDatabase(restoredEngine, disposeOnClose: false);
            db.GetCollection("rows").FindById(1)["value"].AsString.Should().Be("committed");
            db.GetCollection("other").FindById(9)["value"].AsInt32.Should().Be(123);
        }

        private static LiteEngine Open(Stream data, Stream log) =>
            new LiteEngine(new EngineSettings { DataStream = data, LogStream = log, ReadOnly = true });

        private static TempFile Seed()
        {
            var file = new TempFile();
            using var db = new LiteDatabase(file.Filename);
            db.GetCollection("rows").Insert(new BsonDocument { ["_id"] = 1, ["value"] = "committed" });
            db.GetCollection("other").Insert(new BsonDocument { ["_id"] = 9, ["value"] = 123 });
            db.Checkpoint();
            return file;
        }

        private sealed class ObservedFile : FileStream
        {
            internal int HeaderBytes;
            internal int ReadSize = PAGE_SIZE;
            internal int FailAfter = int.MaxValue;
            internal bool Eof;

            internal ObservedFile(string filename)
                : base(filename, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1) { }

            public override int Read(byte[] buffer, int offset, int count)
            {
                if (HeaderBytes >= FailAfter)
                {
                    if (Eof) return 0;
                    throw new IOException("header read failed");
                }
                var position = Position;
                var read = base.Read(buffer, offset, Math.Min(count, Math.Min(ReadSize, FailAfter - HeaderBytes)));
                if (position < PAGE_SIZE) HeaderBytes += (int)Math.Min(read, PAGE_SIZE - position);
                return read;
            }
        }
    }
}
