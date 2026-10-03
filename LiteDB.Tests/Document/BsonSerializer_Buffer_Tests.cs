using System;
using System.Buffers;
using FluentAssertions;
using Xunit;

namespace LiteDB.Tests.Document
{
    public class BsonSerializer_Buffer_Tests
    {
        [Fact]
        public void Serialize_To_Sliced_Pooled_Memory_Preserves_Output()
        {
            var document = CreateDocument();
            var expected = BsonSerializer.Serialize(document);
            var rented = ArrayPool<byte>.Shared.Rent(expected.Length + 4);

            try
            {
                rented[0] = 0x5A;
                rented[1] = 0x5A;
                rented[expected.Length + 2] = 0x5A;
                rented[expected.Length + 3] = 0x5A;
                var destination = new Memory<byte>(rented, 2, expected.Length);

                var written = BsonSerializer.Serialize(document, destination);

                written.Should().Be(expected.Length);
                destination.Span.ToArray().Should().Equal(expected);
                rented[0].Should().Be(0x5A);
                rented[1].Should().Be(0x5A);
                rented[expected.Length + 2].Should().Be(0x5A);
                rented[expected.Length + 3].Should().Be(0x5A);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(rented);
            }
        }

        [Fact]
        public void Serialize_To_Undersized_Memory_Throws()
        {
            var document = CreateDocument();
            var bytesRequired = BsonSerializer.Serialize(document).Length;
            var destination = new Memory<byte>(new byte[bytesRequired - 1]);

            Action serialize = () => BsonSerializer.Serialize(document, destination);

            var exception = serialize.Should().Throw<ArgumentException>().Which;
            exception.ParamName.Should().Be("destination");
        }

        [Fact]
        public void Serialize_To_Non_Array_Backed_Memory_Throws()
        {
            var document = CreateDocument();
            var bytesRequired = BsonSerializer.Serialize(document).Length;

            using (var manager = new NonArrayMemoryManager(bytesRequired))
            {
                Action serialize = () => BsonSerializer.Serialize(document, manager.Memory);

                serialize.Should().Throw<NotSupportedException>()
                    .WithMessage("*backed by a managed array*");
            }
        }

#if !NETFRAMEWORK
        [Fact]
        public void Serialize_To_ArrayBufferWriter_Matches_Byte_Array()
        {
            var document = CreateDocument();
            var expected = BsonSerializer.Serialize(document);
            var bufferWriter = new ArrayBufferWriter<byte>();

            var written = BsonSerializer.Serialize(document, bufferWriter);

            written.Should().Be(expected.Length);
            bufferWriter.WrittenCount.Should().Be(expected.Length);
            bufferWriter.WrittenSpan.ToArray().Should().Equal(expected);
        }

        [Fact]
        public void Serialize_To_Writer_Does_Not_Advance_For_Undersized_Memory()
        {
            var document = CreateDocument();
            var bytesRequired = BsonSerializer.Serialize(document).Length;
            var bufferWriter = new TrackingBufferWriter(new byte[bytesRequired - 1]);

            Action serialize = () => BsonSerializer.Serialize(document, bufferWriter);

            serialize.Should().Throw<ArgumentException>();
            bufferWriter.LastSizeHint.Should().Be(bytesRequired);
            bufferWriter.AdvanceCount.Should().Be(0);
        }

        [Fact]
        public void Serialize_To_Writer_Does_Not_Advance_For_Non_Array_Memory()
        {
            var document = CreateDocument();
            var bytesRequired = BsonSerializer.Serialize(document).Length;

            using (var manager = new NonArrayMemoryManager(bytesRequired))
            {
                var bufferWriter = new TrackingBufferWriter(manager.Memory);

                Action serialize = () => BsonSerializer.Serialize(document, bufferWriter);

                serialize.Should().Throw<NotSupportedException>();
                bufferWriter.LastSizeHint.Should().Be(bytesRequired);
                bufferWriter.AdvanceCount.Should().Be(0);
            }
        }

        private sealed class TrackingBufferWriter : IBufferWriter<byte>
        {
            private readonly Memory<byte> _memory;

            public TrackingBufferWriter(Memory<byte> memory)
            {
                _memory = memory;
            }

            public int AdvanceCount { get; private set; }

            public int LastSizeHint { get; private set; }

            public void Advance(int count)
            {
                AdvanceCount += count;
            }

            public Memory<byte> GetMemory(int sizeHint = 0)
            {
                LastSizeHint = sizeHint;
                return _memory;
            }

            public Span<byte> GetSpan(int sizeHint = 0)
            {
                LastSizeHint = sizeHint;
                return _memory.Span;
            }
        }
#endif

        private static BsonDocument CreateDocument()
        {
            return new BsonDocument
            {
                ["_id"] = 123,
                ["name"] = "buffer serialization",
                ["nested"] = new BsonDocument
                {
                    ["active"] = true,
                    ["values"] = new BsonArray { 1, 2, 3 }
                }
            };
        }

        private sealed class NonArrayMemoryManager : MemoryManager<byte>
        {
            private readonly byte[] _buffer;

            public NonArrayMemoryManager(int length)
            {
                _buffer = new byte[length];
            }

            public override Span<byte> GetSpan()
            {
                return _buffer;
            }

            public override MemoryHandle Pin(int elementIndex = 0)
            {
                throw new NotSupportedException();
            }

            public override void Unpin()
            {
            }

            protected override void Dispose(bool disposing)
            {
            }
        }
    }
}
