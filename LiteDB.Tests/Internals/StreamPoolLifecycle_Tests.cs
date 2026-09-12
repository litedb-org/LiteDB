using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using LiteDB.Engine;
using Xunit;

namespace LiteDB.Internals
{
    public class StreamPoolLifecycle_Tests
    {
        [Fact]
        public void Reader_ReturnedAfterDisposal_IsClosed()
        {
            using var pool = new StreamPool(new TrackingFactory(), false);
            var reader = (TrackingStream)pool.Rent();
            pool.Dispose();
            pool.Return(reader);
            reader.DisposeCount.Should().Be(1);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void DisposedPool_RejectsNewStreams(bool writer)
        {
            var factory = new TrackingFactory();
            using var pool = new StreamPool(factory, false);
            pool.Dispose();
            Action rent = () => { _ = writer ? pool.Writer.Value : pool.Rent(); };
            rent.Should().Throw<ObjectDisposedException>();
            factory.Created.Should().BeNull();
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task Stream_CreatedDuringDisposal_IsClosed(bool writer)
        {
            using var started = new ManualResetEventSlim();
            using var resume = new ManualResetEventSlim();
            var factory = new TrackingFactory
            {
                OnCreate = () =>
                {
                    started.Set();
                    if (!resume.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException();
                }
            };
            using var pool = new StreamPool(factory, false);
            var operation = Task.Run(() =>
            {
                Action rent = () => { _ = writer ? pool.Writer.Value : pool.Rent(); };
                rent.Should().Throw<ObjectDisposedException>();
            });
            try
            {
                started.Wait(TimeSpan.FromSeconds(10)).Should().BeTrue();
                pool.Dispose();
            }
            finally
            {
                resume.Set();
            }
            await operation;
            factory.Created.DisposeCount.Should().Be(1);
        }

        [Fact]
        public void Factory_IsSoleOwner_OfSharedBaseStream()
        {
            var stream = new TrackingStream();
            using var pool = new StreamPool(new StreamFactory(stream, null, true), false);
            var reader = pool.Rent();
            pool.Return(reader);
            _ = pool.Writer.Value;
            pool.Dispose();
            stream.DisposeCount.Should().Be(1);
        }

        private sealed class TrackingFactory : IStreamFactory
        {
            public TrackingStream Created { get; private set; }
            public Action OnCreate { get; set; }
            public string Name => "tracking";
            public bool CloseOnDispose => true;
            public bool Exists() => false;
            public bool IsLocked() => false;
            public long GetLength() => 0;
            public void Delete() { }
            public void Dispose() { }
            public void TrimCapacity(Stream stream) { }
            public Stream GetStream(bool canWrite, bool sequential)
            {
                this.Created = new TrackingStream();
                this.OnCreate?.Invoke();
                return this.Created;
            }
        }

        private sealed class TrackingStream : MemoryStream
        {
            public int DisposeCount { get; private set; }
            protected override void Dispose(bool disposing)
            {
                if (disposing) this.DisposeCount++;
                base.Dispose(disposing);
            }
        }
    }
}
