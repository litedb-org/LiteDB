using System;
using System.Buffers;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using FluentAssertions;
using LiteDB.Engine;
using Xunit;
using static LiteDB.Constants;

namespace LiteDB.Internals
{
    public class BufferLease_Tests
    {
        [Theory]
        [InlineData("number")]
        [InlineData("objectId")]
        [InlineData("string")]
        [InlineData("cstring")]
        public void Reader_Failure_ReturnsRentedArray(string operation)
        {
            var pool = new TrackingPool();
            var bytes = new byte[operation == "cstring" ? 600 : 1];
            for (var i = 0; i < bytes.Length; i++) bytes[i] = 65;
            using var reader = new BufferReader(FailingSource(bytes), bufferPool: pool);
            Action read = () =>
            {
                switch (operation)
                {
                    case "number": reader.ReadInt64(); break;
                    case "objectId": reader.ReadObjectId(); break;
                    case "string": reader.ReadString(1000); break;
                    case "cstring": reader.ReadCString(); break;
                }
            };
            read.Should().Throw<IOException>();
            // CString uses MemoryStream on .NET Framework instead of ArrayPool.
            if (operation != "cstring") pool.Rented.Should().BeGreaterThan(0);
            pool.Outstanding.Should().BeEmpty();
        }

        [Theory]
        [InlineData("number")]
        [InlineData("objectId")]
        [InlineData("string")]
        [InlineData("cstring")]
        public void Writer_Failure_ReturnsRentedArray(string operation)
        {
            var pool = new TrackingPool();
            using var writer = new BufferWriter(FailingSource(new byte[1]), pool);
            Action write = () =>
            {
                switch (operation)
                {
                    case "number": writer.Write(42L); break;
                    case "objectId": writer.Write(ObjectId.NewObjectId()); break;
                    case "string": writer.WriteString(new string('x', 1000), false); break;
                    case "cstring": writer.WriteCString(new string('x', 1000)); break;
                }
            };
            write.Should().Throw<IOException>();
            pool.Rented.Should().BeGreaterThan(0);
            pool.Outstanding.Should().BeEmpty();
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void Constructor_Failure_DisposesBufferSource(bool writer)
        {
            var source = new FailingEnumerator();
            Action create = () =>
            {
                if (writer) _ = new BufferWriter(source);
                else _ = new BufferReader(source);
            };
            create.Should().Throw<IOException>();
            source.Disposed.Should().BeTrue();
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void SortReader_StoppedEarlyOrFailed_ReturnsRentedArray(bool fail)
        {
            var pool = new TrackingPool();
            var bytes = new byte[PAGE_SIZE];
            var buffer = new BufferSlice(bytes, 0, bytes.Length);
            using var container = new SortContainer(Collation.Binary, PAGE_SIZE, new[] { Query.Ascending }, pool);
            container.Insert(new[] { new KeyValuePair<BsonValue, PageAddress>(42, new PageAddress(1, 0)) }, Query.Ascending, buffer);
            using Stream stream = fail ? new FailingStream(bytes) : new MemoryStream(bytes);
            container.Position = 0;
            if (fail)
            {
                Action initialize = () => container.InitializeReader(stream, null, false);
                initialize.Should().Throw<IOException>();
            }
            else
            {
                container.InitializeReader(stream, null, false);
                pool.Outstanding.Should().HaveCount(1);
            }
            container.Dispose();
            container.Dispose();
            pool.Rented.Should().Be(1);
            pool.Outstanding.Should().BeEmpty();
        }

        private static IEnumerable<BufferSlice> FailingSource(byte[] bytes)
        {
            yield return new BufferSlice(bytes, 0, bytes.Length);
            throw new IOException("source failed");
        }

        private sealed class TrackingPool : ArrayPool<byte>
        {
            public HashSet<byte[]> Outstanding { get; } = new HashSet<byte[]>();
            public int Rented { get; private set; }
            public override byte[] Rent(int minimumLength)
            {
                var buffer = new byte[minimumLength];
                this.Outstanding.Add(buffer);
                this.Rented++;
                return buffer;
            }
            public override void Return(byte[] array, bool clearArray = false)
            {
                this.Outstanding.Remove(array).Should().BeTrue("each rental must be returned exactly once");
                if (clearArray) Array.Clear(array, 0, array.Length);
            }
        }

        private sealed class FailingStream : MemoryStream
        {
            public FailingStream(byte[] buffer) : base(buffer) { }
            public override int Read(byte[] buffer, int offset, int count) => throw new IOException("read failed");
        }

        private sealed class FailingEnumerator : IEnumerable<BufferSlice>, IEnumerator<BufferSlice>
        {
            public bool Disposed { get; private set; }
            public BufferSlice Current => throw new InvalidOperationException();
            object IEnumerator.Current => this.Current;
            public IEnumerator<BufferSlice> GetEnumerator() => this;
            IEnumerator IEnumerable.GetEnumerator() => this.GetEnumerator();
            public bool MoveNext() => throw new IOException("source failed");
            public void Reset() => throw new NotSupportedException();
            public void Dispose() => this.Disposed = true;
        }
    }
}
