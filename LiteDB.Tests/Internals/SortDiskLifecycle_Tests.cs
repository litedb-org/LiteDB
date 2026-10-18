using System;
using System.IO;
using FluentAssertions;
using LiteDB.Engine;
using LiteDB.Tests;
using Xunit;

namespace LiteDB.Internals
{
    public class SortDiskLifecycle_Tests
    {
        [Theory]
        [InlineData(null)]
        [InlineData(false)]
        [InlineData(true)]
        public void Dispose_DeletesScratchFile_AndPreservesDatabase(bool? failingWriter)
        {
            using var file = new TempFile();
            using (var db = new LiteDatabase(file.Filename))
            {
                db.GetCollection("items").Insert(new BsonDocument { ["_id"] = 1, ["value"] = "committed" });
            }
            var original = File.ReadAllBytes(file.Filename);
            var scratch = FileHelper.GetSuffixFile(file.Filename, "-tmp");
            var factory = new FailingCloseFactory(scratch, failingWriter);
            using var disk = new SortDisk(factory, Constants.PAGE_SIZE, new EnginePragmas(null));
            try
            {
                var bytes = new byte[Constants.PAGE_SIZE];
                disk.Write(disk.GetContainerPosition(), new BufferSlice(bytes, 0, bytes.Length));
                var reader = disk.GetReader();
                disk.Return(reader);
                File.Exists(scratch).Should().BeTrue();
                new FileInfo(scratch).Length.Should().BeGreaterThan(0);

                Action dispose = () => disk.Dispose();
                if (failingWriter.HasValue)
                {
                    dispose.Should().Throw<AggregateException>()
                        .Which.InnerExceptions.Should().ContainSingle().Which.Should().BeSameAs(factory.Failure);
                }
                else
                {
                    dispose.Should().NotThrow();
                }

                File.Exists(scratch).Should().BeFalse();
                dispose.Should().NotThrow();
                File.Exists(scratch).Should().BeFalse();
                File.ReadAllBytes(file.Filename).Should().Equal(original);
                for (var i = 0; i < 2; i++)
                {
                    using var reopened = new LiteDatabase(file.Filename);
                    var items = reopened.GetCollection("items");
                    items.Count().Should().Be(1);
                    items.FindById(1)["value"].AsString.Should().Be("committed");
                }
            }
            finally
            {
                // Also clean up on the known-bad implementation, after the pool has closed.
                disk.Dispose();
                File.Delete(scratch);
            }
        }

        private sealed class FailingCloseFactory : IStreamFactory
        {
            private readonly string _filename;
            private readonly bool? _failingWriter;
            private readonly FileStreamFactory _inner;

            public FailingCloseFactory(string filename, bool? failingWriter)
            {
                _filename = filename;
                _failingWriter = failingWriter;
                _inner = new FileStreamFactory(filename, null, false, false);
            }

            public IOException Failure { get; } = new IOException("Forced scratch stream close failure");
            public string Name => _inner.Name;
            public bool CloseOnDispose => true;
            public bool Exists() => _inner.Exists();
            public bool IsLocked() => _inner.IsLocked();
            public long GetLength() => _inner.GetLength();
            public void Delete() => _inner.Delete();
            public void Dispose() => _inner.Dispose();
            public void TrimCapacity(Stream stream) => _inner.TrimCapacity(stream);

            public Stream GetStream(bool canWrite, bool sequential)
            {
                return _failingWriter == canWrite
                    ? new FailingCloseStream(_filename, canWrite, this.Failure)
                    : _inner.GetStream(canWrite, sequential);
            }
        }

        private sealed class FailingCloseStream : FileStream
        {
            private readonly IOException _failure;

            public FailingCloseStream(string filename, bool canWrite, IOException failure)
                : base(filename, FileMode.OpenOrCreate, canWrite ? FileAccess.ReadWrite : FileAccess.Read,
                    canWrite ? FileShare.Read : FileShare.ReadWrite)
            {
                _failure = failure;
            }

            protected override void Dispose(bool disposing)
            {
                // Release the OS handle, then model a close-time I/O failure.
                base.Dispose(disposing);
                if (disposing) throw _failure;
            }
        }
    }
}
