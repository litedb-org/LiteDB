using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using LiteDB.Engine;
using LiteDB.Tests;
using Xunit;

namespace LiteDB.Internals
{
    public class StreamOwnership_Tests
    {
        [Fact]
        public void Concurrent_Factory_Disposal_Closes_Owned_Stream_Once()
        {
            var stream = new TrackingMemoryStream();
            var factory = new StreamFactory(stream, null, true);
            Parallel.For(0, 100, _ => factory.Dispose());
            stream.DisposeCount.Should().Be(1);
        }

        [Fact]
        public void CallerOwned_Stream_RemainsOpen_AfterPoolDispose()
        {
            using var stream = new TrackingMemoryStream();
            var factory = new StreamFactory(stream, null, false);
            var pool = new StreamPool(factory, false);

            var reader = pool.Rent();
            pool.Return(reader);
            _ = pool.Writer.Value;

            pool.Dispose();

            stream.WasDisposed.Should().BeFalse();
            stream.CanWrite.Should().BeTrue();
        }

        [Fact]
        public void EngineOwned_Stream_IsDisposed_AfterPoolDispose()
        {
            var stream = new TrackingMemoryStream();
            var factory = new StreamFactory(stream, null, true);
            var pool = new StreamPool(factory, false);
            _ = pool.Writer.Value;

            pool.Dispose();

            stream.WasDisposed.Should().BeTrue();
        }

        [Fact]
        public void StreamFactory_ConcurrentDispose_DisposesOwnedStreamOnce()
        {
            var stream = new TrackingMemoryStream();
            var factory = new StreamFactory(stream, null, true);

            Parallel.For(0, 64, _ => factory.Dispose());

            stream.DisposeCount.Should().Be(1);
        }

        [Fact]
        public void StreamPool_ConcurrentDispose_DisposesFactoryOnce()
        {
            var factory = new CountingFactory();
            var pool = new StreamPool(factory, false);

            Parallel.For(0, 64, _ => pool.Dispose());

            factory.DisposeCalls.Should().Be(1);
        }

        [Fact]
        public void DiskService_ConcurrentDispose_CompletesWithoutDoubleCleanup()
        {
            var settings = new EngineSettings
            {
                DataStream = new MemoryStream(),
                LogStream = new MemoryStream()
            };
            var disk = new DiskService(settings, new EngineState(null, settings), new[] { 2 });

            Action dispose = () => Parallel.For(0, 64, _ => disk.Dispose());

            dispose.Should().NotThrow();
        }

        [Theory]
        [InlineData(null)]
        [InlineData("secret")]
        public void OwnedMemoryStream_TrimCapacity_UsesPhysicalLength(string password)
        {
            var stream = new MemoryStream();
            using var factory = new StreamFactory(stream, password, true);
            using var wrapper = factory.GetStream(true, false);

            stream.Capacity = 1024 * 1024;
            wrapper.SetLength(0);
            factory.TrimCapacity(wrapper);

            stream.Capacity.Should().Be((int)stream.Length);
        }

        [Fact]
        public void CallerOwnedMemoryStream_IsNotTrimmed()
        {
            using var stream = new MemoryStream { Capacity = 1024 * 1024 };
            using var factory = new StreamFactory(stream, null, false);
            using var wrapper = factory.GetStream(true, false);

            factory.TrimCapacity(wrapper);

            stream.Capacity.Should().Be(1024 * 1024);
        }

        [Fact]
        public void TempStream_DeletedOnDispose()
        {
            var filename = Path.Combine(Path.GetTempPath(), "litedb-stream-" + Guid.NewGuid() + ".db");
            var stream = new TempStream(filename, 1);

            stream.Seek(2, SeekOrigin.Begin);
            stream.WriteByte(1);
            File.Exists(filename).Should().BeTrue();

            stream.Dispose();

            File.Exists(filename).Should().BeFalse();
        }

        [Fact]
        public void HiddenFile_SetAttributesFailure_DisposesStream()
        {
            using var file = new TempFile();
            File.Delete(file.Filename);
            var factory = new FileStreamFactory(
                file.Filename,
                null,
                false,
                true,
                true,
                _ => throw new IOException("injected attribute failure"));

            Action open = () => factory.GetStream(true, false);

            open.Should().Throw<IOException>().WithMessage("injected attribute failure");
            using var exclusive = new System.IO.FileStream(
                file.Filename,
                FileMode.Open,
                FileAccess.ReadWrite,
                FileShare.None,
                4096,
                FileOptions.None);
        }

        [Fact]
        public void LiteDatabase_DoesNotDisposeCallerStreams()
        {
            using var data = new TrackingMemoryStream();
            using var log = new TrackingMemoryStream();

            using (var database = new LiteDatabase(data, logStream: log))
            {
                database.GetCollection("docs").Insert(new BsonDocument { ["_id"] = 1 });
            }

            data.WasDisposed.Should().BeFalse();
            log.WasDisposed.Should().BeFalse();
            data.CanRead.Should().BeTrue();
            log.CanRead.Should().BeTrue();
        }

        [Fact]
        public void DiskServiceCtor_Failure_DisposesWrappers_ButNotCallerStreams()
        {
            using var data = new TrackingMemoryStream();
            using var log = new ThrowingLengthStream();
            var settings = new EngineSettings
            {
                DataStream = data,
                LogStream = log
            };
            var state = new EngineState(null, settings);

            Action create = () => new DiskService(settings, state, new[] { 2 });

            create.Should().Throw<IOException>().WithMessage("injected length failure");
            data.WasDisposed.Should().BeFalse();
            log.WasDisposed.Should().BeFalse();
        }

        private class TrackingMemoryStream : MemoryStream
        {
            private int _disposeCount;
            public int DisposeCount => Volatile.Read(ref _disposeCount);
            public bool WasDisposed { get; private set; }

            protected override void Dispose(bool disposing)
            {
                if (disposing)
                {
                    this.WasDisposed = true;
                    Interlocked.Increment(ref _disposeCount);
                }
                base.Dispose(disposing);
            }

        }

        private sealed class CountingFactory : IStreamFactory
        {
            private int _disposeCalls;

            public int DisposeCalls => _disposeCalls;
            public string Name => ":counting:";
            public bool CloseOnDispose => false;
            public Stream GetStream(bool canWrite, bool sequencial) => new MemoryStream();
            public long GetLength() => 0;
            public bool Exists() => false;
            public void Delete() { }
            public bool IsLocked() => false;
            public void TrimCapacity(Stream stream) { }
            public void Dispose() => Interlocked.Increment(ref _disposeCalls);
        }

        private sealed class ThrowingLengthStream : TrackingMemoryStream
        {
            public override long Length => throw new IOException("injected length failure");
        }
    }
}
