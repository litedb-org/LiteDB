using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using FluentAssertions;
using LiteDB.Engine;
using LiteDB.Tests;
using Xunit;
using static LiteDB.Constants;

namespace LiteDB.Internals
{
    public class WalChecksumFailure_Tests
    {
        [Theory]
        [InlineData(null)]
        [InlineData("secret")]
        public void RebuildReader_UsesTheSameVerifiedCommittedPrefix(string password)
        {
            using var test = new WalTestDatabase(password);
            test.Seed("docs");
            test.Database.Checkpoint();
            test.Update("docs", 1);
            var end = checked((int)test.Log.Length);
            test.Update("docs", 2);
            test.Update("docs", 3);
            var bytes = test.Log.ToArray();
            Array.Clear(bytes, end + WalChecksum.FrameSize, WalChecksum.FrameSize);
            using var data = ChecksumTestFiles.Copy(test.Data.ToArray());
            using var log = ChecksumTestFiles.Copy(bytes);
            var errors = new List<FileReaderError>();
            using var reader = new FileReaderV8(new EngineSettings { DataStream = data, LogStream = log, Password = password }, errors);
            reader.Open();
            reader.GetDocuments("docs").Should().HaveCount(WalTestDatabase.DocumentCount)
                .And.OnlyContain(x => x["value"].AsInt32 == 1);
            errors.Should().ContainSingle().Which.Message.Should().Contain("Checksum");
            log.ToArray().Should().Equal(bytes);
        }

        [Theory]
        [InlineData(null, false)]
        [InlineData(null, true)]
        [InlineData("secret", false)]
        [InlineData("secret", true)]
        public void FailedGenerationPublication_StopsWrites_AndCanRecover(string password, bool flush)
        {
            using var data = new GenerationFailureStream { HeaderPosition = password == null ? 0 : PAGE_SIZE, FailFlush = flush };
            using var log = new MemoryStream();
            var settings = new EngineSettings { DataStream = data, LogStream = log, Password = password };
            using (var engine = new LiteEngine(settings))
            using (var db = new LiteDatabase(engine, disposeOnClose: false))
            {
                db.CheckpointSize = 0;
                var docs = db.GetCollection("docs");
                docs.Insert(new BsonDocument { ["_id"] = 1, ["value"] = 0 });
                db.Checkpoint();
                docs.Update(new BsonDocument { ["_id"] = 1, ["value"] = 1 });
                data.Armed = true;
                Action checkpoint = () => db.Checkpoint();
                checkpoint.Should().Throw<IOException>().WithMessage("generation publication failed");
                Action write = () => docs.Insert(new BsonDocument { ["_id"] = 2 });
                write.Should().Throw<IOException>();
            }
            using var reopenedEngine = new LiteEngine(settings);
            using var reopened = new LiteDatabase(reopenedEngine, disposeOnClose: false);
            reopened.GetCollection("docs").FindAll().Should().ContainSingle()
                .Which["value"].AsInt32.Should().Be(1);
            reopened.GetCollection("docs").Insert(new BsonDocument { ["_id"] = 2 });
            reopened.Checkpoint();
        }

        [Fact]
        public void OperationalReadError_DoesNotTruncateTheWal()
        {
            using var test = new WalTestDatabase(null);
            test.Seed("docs");
            var bytes = test.Log.ToArray();
            using var data = ChecksumTestFiles.Copy(test.Data.ToArray());
            using var log = new ReadFailureStream(bytes);
            Action open = () => { using var engine = new LiteEngine(new EngineSettings { DataStream = data, LogStream = log }); };
            open.Should().Throw<IOException>().WithMessage("transient read failure");
            log.ToArray().Should().Equal(bytes);
        }

        [Fact]
        public void ReadOnlyOpen_PreservesAPartialWalEvenWhenNoFrameCanBeRead()
        {
            using var file = new TempFile();
            using (var db = new LiteDatabase(file.Filename)) db.GetCollection("docs").Insert(new BsonDocument { ["_id"] = 1 });
            var logPath = Path.Combine(Path.GetDirectoryName(file.Filename), Path.GetFileNameWithoutExtension(file.Filename) + "-log.db");
            var bytes = new byte[127];
            new Random(2935).NextBytes(bytes);
            File.WriteAllBytes(logPath, bytes);
            try
            {
                using (var db = new LiteDatabase(new ConnectionString { Filename = file.Filename, ReadOnly = true }))
                {
                    db.GetCollection("docs").Count().Should().Be(1);
                    db.GetCollection("$database").FindAll().Single()["recoveryDiscardedWalBytes"].AsInt64.Should().Be(bytes.Length);
                }
                File.ReadAllBytes(logPath).Should().Equal(bytes);
            }
            finally { File.Delete(logPath); }
        }

        private sealed class GenerationFailureStream : MemoryStream
        {
            internal bool Armed;
            internal bool FailFlush;
            internal long HeaderPosition;
            private bool _headerWritten;

            public override void Write(byte[] buffer, int offset, int count)
            {
                if (Armed && Position == HeaderPosition && count == PAGE_SIZE)
                {
                    if (!FailFlush) Fail();
                    _headerWritten = true;
                }
                base.Write(buffer, offset, count);
            }

            public override void Flush()
            {
                if (Armed && FailFlush && _headerWritten) Fail();
                base.Flush();
            }

            private void Fail()
            {
                Armed = false;
                throw new IOException("generation publication failed");
            }
        }

        private sealed class ReadFailureStream : MemoryStream
        {
            internal ReadFailureStream(byte[] bytes) : base(bytes) { }
            public override int Read(byte[] buffer, int offset, int count)
            {
                if (Position >= WalChecksum.FrameSize) throw new IOException("transient read failure");
                return base.Read(buffer, offset, count);
            }
        }
    }
}
