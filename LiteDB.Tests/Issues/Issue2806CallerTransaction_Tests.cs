using System;
using System.IO;
using System.Linq;
using FluentAssertions;
using Xunit;

namespace LiteDB.Tests.Issues
{
    public class Issue2806CallerTransaction_Tests
    {
        private sealed class ScriptedSource : Stream
        {
            private readonly long _length;
            private readonly long _failAt;
            private long _position;

            public ScriptedSource(long length, long failAt = long.MaxValue)
            {
                _length = length;
                _failAt = failAt;
            }

            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => throw new NotSupportedException();
            public override long Position { get => _position; set => throw new NotSupportedException(); }

            public override int Read(byte[] buffer, int offset, int count)
            {
                if (_position >= _failAt) throw new IOException("source interrupted");
                var read = (int)Math.Min(count, Math.Min(_failAt, _length) - _position);
                for (var i = 0; i < read; i++) buffer[offset + i] = 9;
                _position += read;
                return read;
            }

            public override void Flush() { }
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        }

        private static byte[] Pattern(int length) => Enumerable.Range(0, length).Select(i => (byte)(i * 13)).ToArray();

        private static int[] Values(ILiteDatabase db) =>
            db.GetCollection("docs").FindAll().Select(doc => doc["a"].AsInt32).OrderBy(a => a).ToArray();

        [Theory]
        [InlineData(ConnectionType.Direct, true)]
        [InlineData(ConnectionType.Shared, true)]
        [InlineData(ConnectionType.Direct, false)]
        public void Source_failure_inside_a_caller_transaction_undoes_only_the_upload(ConnectionType connection, bool replacesFile)
        {
            using var file = new TempFile();
            var original = Pattern(620000);
            using (var db = new LiteDatabase(new ConnectionString { Filename = file.Filename, Connection = connection }))
            {
                var docs = db.GetCollection("docs");
                if (replacesFile) db.FileStorage.Upload("asset", "old.bin", new MemoryStream(original));
                docs.Insert(new BsonDocument { ["a"] = 2 });

                db.BeginTrans().Should().BeTrue();
                docs.Insert(new BsonDocument { ["a"] = 3 });
                using var source = new ScriptedSource(900000, failAt: 600000);
                Action upload = () => db.FileStorage.Upload("asset", "new.bin", source);
                upload.Should().Throw<IOException>().WithMessage("source interrupted");
                docs.Insert(new BsonDocument { ["a"] = 4 });
                db.Commit().Should().BeTrue("a source failure never reached the engine, so the caller still decides");
            }
            using var reopened = new LiteDatabase(file.Filename);
            Values(reopened).Should().Equal(2, 3, 4);
            reopened.FileStorage.Exists("asset").Should().Be(replacesFile);
            reopened.GetCollection("_chunks").Count().Should().Be(replacesFile ? 3 : 0, "no partial chunk may survive");
            if (!replacesFile) return;
            using var download = new MemoryStream();
            reopened.FileStorage.Download("asset", download);
            download.ToArray().Should().Equal(original);
            reopened.FileStorage.FindById("asset").Filename.Should().Be("old.bin");
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void Replacement_inside_a_caller_transaction_follows_the_callers_decision(bool commit)
        {
            using var db = new LiteDatabase(":memory:");
            var original = Pattern(620000);
            db.FileStorage.Upload("asset", "old.bin", new MemoryStream(original));

            db.BeginTrans().Should().BeTrue();
            using (var source = new ScriptedSource(300000)) db.FileStorage.Upload("asset", "new.bin", source);
            (commit ? db.Commit() : db.Rollback()).Should().BeTrue();

            using var download = new MemoryStream();
            db.FileStorage.Download("asset", download);
            download.ToArray().Should().Equal(commit ? Enumerable.Repeat((byte)9, 300000).ToArray() : original);
            db.FileStorage.FindById("asset").Filename.Should().Be(commit ? "new.bin" : "old.bin");
            var chunks = db.GetCollection("_chunks").FindAll().Select(chunk => chunk["_id"]["n"].AsInt32).OrderBy(n => n).ToArray();
            chunks.Should().Equal(Enumerable.Range(0, db.FileStorage.FindById("asset").Chunks), "no staged or replaced chunk may remain");
        }
    }
}
