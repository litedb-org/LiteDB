using System;
using System.IO;
using System.Linq;
using System.Threading;
using FluentAssertions;
using Xunit;

namespace LiteDB.Tests.Database
{
    public class StorageAtomicity_Tests
    {
        private sealed class InterruptedSource : Stream
        {
            private long _position;
            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => throw new NotSupportedException();
            public override long Position { get => _position; set => throw new NotSupportedException(); }
            public override int Read(byte[] buffer, int offset, int count)
            {
                if (_position >= 310000) throw new IOException("source interrupted");
                var read = (int)Math.Min(count, 310000 - _position);
                Array.Clear(buffer, offset, read);
                _position += read;
                return read;
            }
            public override void Flush() { }
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        }

        [Theory]
        [InlineData(ConnectionType.Direct, false, null)]
        [InlineData(ConnectionType.Direct, true, null)]
        [InlineData(ConnectionType.Shared, false, null)]
        [InlineData(ConnectionType.Shared, true, null)]
        [InlineData(ConnectionType.Direct, false, "secret")]
        [InlineData(ConnectionType.Shared, true, "secret")]
        public void Failed_upload_restores_metadata_and_releases_its_transaction(ConnectionType connection, bool callerTransaction, string password)
        {
            using var file = new TempFile();
            var original = Enumerable.Range(0, 620000).Select(i => (byte)(i * 17)).ToArray();
            using (var db = new LiteDatabase(new ConnectionString { Filename = file.Filename, Connection = connection, Password = password }))
            {
                var storage = db.GetStorage<int>();
                storage.Upload(7, "old.bin", new MemoryStream(original), new BsonDocument { ["version"] = 1 });
                if (callerTransaction)
                {
                    db.BeginTrans();
                    db.GetCollection("caller").Insert(new BsonDocument { ["_id"] = 1 });
                }
                using var source = new InterruptedSource();
                Action upload = () => storage.Upload(7, "new.bin", source, new BsonDocument { ["version"] = 2 });
                upload.Should().Throw<IOException>().WithMessage("source interrupted");
                db.Rollback().Should().BeFalse("a failed upload rolls back even a caller-owned transaction");
                db.GetCollection("caller").Count().Should().Be(0);
                using var restored = new MemoryStream();
                storage.Download(7, restored);
                restored.ToArray().Should().Equal(original);
                storage.FindById(7).Metadata["version"].AsInt32.Should().Be(1);
                storage.FindById(7).Filename.Should().Be("old.bin");

                long workerLength = -1;
                Exception workerError = null;
                var worker = new Thread(() =>
                {
                    try { workerLength = storage.FindById(7).Length; }
                    catch (Exception ex) { workerError = ex; }
                }) { IsBackground = true };
                worker.Start();
                worker.Join(TimeSpan.FromSeconds(5)).Should().BeTrue(
                    "the original owner is still alive and each Shared mutex acquisition must be released");
                workerError.Should().BeNull();
                workerLength.Should().Be(original.Length);
            }
            using var reopened = new LiteDatabase(new ConnectionString { Filename = file.Filename, Password = password });
            using var download = new MemoryStream();
            reopened.GetStorage<int>().Download(7, download);
            download.ToArray().Should().Equal(original);
        }

        [Theory]
        [InlineData(ConnectionType.Direct)]
        [InlineData(ConnectionType.Shared)]
        public void Successful_upload_does_not_commit_a_callers_transaction(ConnectionType connection)
        {
            using var file = new TempFile();
            using var db = new LiteDatabase(new ConnectionString { Filename = file.Filename, Connection = connection });
            db.BeginTrans();
            db.GetCollection("caller").Insert(new BsonDocument { ["_id"] = 1 });
            db.FileStorage.Upload("asset", "new.bin", new MemoryStream(new byte[300000]));
            db.Rollback().Should().BeTrue();
            db.FileStorage.Exists("asset").Should().BeFalse();
            db.GetCollection("_chunks").Count().Should().Be(0);
            db.GetCollection("caller").Count().Should().Be(0);
        }

        [Theory]
        [InlineData(0)]
        [InlineData(1)]
        [InlineData(2)]
        public void Missing_first_middle_or_last_chunk_is_an_explicit_error(int missing)
        {
            using var db = new LiteDatabase(":memory:");
            db.FileStorage.Upload("asset", "original.bin", new MemoryStream(new byte[620000]));
            db.GetCollection("_chunks").Delete(new BsonDocument { ["f"] = "asset", ["n"] = missing }).Should().BeTrue();
            Action read = () => db.FileStorage.Download("asset", new MemoryStream());
            read.Should().Throw<LiteException>().Which.ErrorCode.Should().Be(LiteException.INVALID_FORMAT);
            db.FileStorage.Upload("asset", "replacement.bin", new MemoryStream(new byte[] { 1, 2, 3 }));
            using var download = new MemoryStream();
            db.FileStorage.Download("asset", download);
            download.ToArray().Should().Equal(1, 2, 3);
        }

        [Theory]
        [InlineData(0)]
        [InlineData(LiteFileStream<string>.MAX_CHUNK_SIZE)]
        [InlineData(LiteFileStream<string>.MAX_CHUNK_SIZE * 2)]
        public void Empty_and_exact_chunk_boundaries_support_seek_and_eof(int length)
        {
            using var db = new LiteDatabase(":memory:");
            var bytes = Enumerable.Range(0, length).Select(i => (byte)(i % 251)).ToArray();
            db.FileStorage.Upload("asset", "test.bin", new MemoryStream(bytes));
            using var reader = db.FileStorage.OpenRead("asset");
            foreach (var position in new[] { 0, LiteFileStream<string>.MAX_CHUNK_SIZE - 1, LiteFileStream<string>.MAX_CHUNK_SIZE, length })
            {
                reader.Seek(position, SeekOrigin.Begin);
                reader.ReadByte().Should().Be(position >= length ? -1 : bytes[position]);
            }
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void Empty_chunk_or_zero_length_metadata_cannot_silently_hide_file_content(bool emptyChunk)
        {
            using var db = new LiteDatabase(":memory:");
            db.FileStorage.Upload("asset", "test.bin", new MemoryStream(new byte[5000]));
            var collection = db.GetCollection(emptyChunk ? "_chunks" : "_files");
            var document = collection.FindAll().Single();
            if (emptyChunk) document["data"] = new byte[0];
            else document["length"] = 0L;
            collection.Update(document);
            Action read = () => db.FileStorage.Download("asset", new MemoryStream());
            read.Should().Throw<LiteException>().Which.ErrorCode.Should().Be(LiteException.INVALID_FORMAT);
        }
    }
}
