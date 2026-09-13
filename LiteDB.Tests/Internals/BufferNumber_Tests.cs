using System;
using System.Collections.Generic;
using FluentAssertions;
using LiteDB.Engine;
using Xunit;
using static LiteDB.Constants;

namespace LiteDB.Internals
{
    public class BufferNumber_Tests
    {
        private const UInt16 UInt16Value = 0x1234;
        private const Int32 Int32Value = -0x1234567;
        private const UInt32 UInt32Value = 0x89ABCDEF;
        private const Int64 Int64Value = -0x0102030405060708L;
        private const Single SingleValue = -1234.5f;
        private const Double DoubleValue = 123456789.987654321d;
        private const Decimal DecimalValue = 123456789.987654321m;
        private const int ByteCount = 46;

        [Fact]
        public void Numbers_Use_Native_Byte_Order()
        {
            var buffer = new byte[ByteCount];

            using (var writer = new BufferWriter(buffer))
            {
                WriteValues(writer);
            }

            var expected = new List<byte>(ByteCount);
            expected.AddRange(BitConverter.GetBytes(UInt16Value));
            expected.AddRange(BitConverter.GetBytes(Int32Value));
            expected.AddRange(BitConverter.GetBytes(UInt32Value));
            expected.AddRange(BitConverter.GetBytes(Int64Value));
            expected.AddRange(BitConverter.GetBytes(SingleValue));
            expected.AddRange(BitConverter.GetBytes(DoubleValue));
            foreach (var bit in Decimal.GetBits(DecimalValue))
            {
                expected.AddRange(BitConverter.GetBytes(bit));
            }

            buffer.Should().Equal(expected);

            using (var reader = new BufferReader(expected.ToArray()))
            {
                ReadValues(reader);
            }
        }

        [Fact]
        public void Numbers_Cross_Segment_Boundaries()
        {
            var buffer = new byte[ByteCount];
            var segments = CreateCrossingSegments(buffer, 1);

            using (var writer = new BufferWriter(segments))
            {
                WriteValues(writer);
                writer.Position.Should().Be(ByteCount);
            }

            using (var reader = new BufferReader(segments))
            {
                ReadValues(reader);
                reader.Position.Should().Be(ByteCount);
            }
        }

        [Theory]
        [InlineData(0)]
        [InlineData(1)]
        [InlineData(3)]
        [InlineData(7)]
        public void Int64_Rejects_Short_Buffer(int length)
        {
            Action read = () =>
            {
                using var reader = new BufferReader(new byte[length]);
                reader.ReadInt64();
            };
            Action write = () =>
            {
                using var writer = new BufferWriter(new byte[length]);
                writer.Write(Int64Value);
            };

            read.Should().Throw<LiteException>();
            write.Should().Throw<LiteException>();
        }

        [Fact]
        public void Number_Fast_Paths_Validate_Buffer_Ownership()
        {
            var page = new PageBuffer(new byte[PAGE_SIZE], 0, 1);
            page.Write(Int64Value, 0);

            var readSlice = page.Slice(0, sizeof(Int64));
            var writeSlice = page.Slice(sizeof(Int64), sizeof(Int64));
            page.Generation++;

            Action read = () =>
            {
                using var reader = new BufferReader(readSlice);
                reader.ReadInt64();
            };
            Action write = () =>
            {
                using var writer = new BufferWriter(writeSlice);
                writer.Write(Int64Value);
            };

            read.Should().Throw<LiteException>();
            write.Should().Throw<LiteException>();
        }

#if !NETFRAMEWORK
        [Fact]
        public void Number_Reads_And_Writes_Do_Not_Allocate_Per_Value()
        {
            const int count = 64;

            WarmUpAllocationPaths();

            var contiguousBuffer = new byte[count * ByteCount];
            using (var writer = new BufferWriter(contiguousBuffer))
            {
                MeasureWrites(writer, count).Should().Be(0);
            }
            using (var reader = new BufferReader(contiguousBuffer))
            {
                MeasureReads(reader, count).Should().Be(0);
            }

            var segmentedBuffer = new byte[count * ByteCount];
            var segments = CreateCrossingSegments(segmentedBuffer, count);
            using (var writer = new BufferWriter(segments))
            {
                MeasureWrites(writer, count).Should().Be(0);
            }
            using (var reader = new BufferReader(segments))
            {
                MeasureReads(reader, count).Should().Be(0);
            }
        }

        private static void WarmUpAllocationPaths()
        {
            var contiguous = new byte[ByteCount];
            using (var writer = new BufferWriter(contiguous)) MeasureWrites(writer, 1);
            using (var reader = new BufferReader(contiguous)) MeasureReads(reader, 1);

            var segmented = new byte[ByteCount];
            var segments = CreateCrossingSegments(segmented, 1);
            using (var writer = new BufferWriter(segments)) MeasureWrites(writer, 1);
            using (var reader = new BufferReader(segments)) MeasureReads(reader, 1);
        }

        private static long MeasureWrites(BufferWriter writer, int count)
        {
            var before = GC.GetAllocatedBytesForCurrentThread();
            for (var i = 0; i < count; i++) WriteValues(writer);
            return GC.GetAllocatedBytesForCurrentThread() - before;
        }

        private static long MeasureReads(BufferReader reader, int count)
        {
            var before = GC.GetAllocatedBytesForCurrentThread();
            for (var i = 0; i < count; i++) ReadValuesWithoutAssertions(reader);
            return GC.GetAllocatedBytesForCurrentThread() - before;
        }

        private static void ReadValuesWithoutAssertions(BufferReader reader)
        {
            reader.ReadUInt16();
            reader.ReadInt32();
            reader.ReadUInt32();
            reader.ReadInt64();
            reader.ReadSingle();
            reader.ReadDouble();
            reader.ReadDecimal();
        }
#endif

        private static BufferSlice[] CreateCrossingSegments(byte[] buffer, int repetitions)
        {
            var sizes = new[] { 2, 4, 4, 8, 4, 8, 4, 4, 4, 4 };
            var segments = new BufferSlice[sizes.Length * repetitions * 2];
            var offset = 0;
            var index = 0;

            for (var repetition = 0; repetition < repetitions; repetition++)
            {
                foreach (var size in sizes)
                {
                    segments[index++] = new BufferSlice(buffer, offset, 1);
                    segments[index++] = new BufferSlice(buffer, offset + 1, size - 1);
                    offset += size;
                }
            }

            return segments;
        }

        private static void WriteValues(BufferWriter writer)
        {
            writer.Write(UInt16Value);
            writer.Write(Int32Value);
            writer.Write(UInt32Value);
            writer.Write(Int64Value);
            writer.Write(SingleValue);
            writer.Write(DoubleValue);
            writer.Write(DecimalValue);
        }

        private static void ReadValues(BufferReader reader)
        {
            reader.ReadUInt16().Should().Be(UInt16Value);
            reader.ReadInt32().Should().Be(Int32Value);
            reader.ReadUInt32().Should().Be(UInt32Value);
            reader.ReadInt64().Should().Be(Int64Value);
            reader.ReadSingle().Should().Be(SingleValue);
            reader.ReadDouble().Should().Be(DoubleValue);
            reader.ReadDecimal().Should().Be(DecimalValue);
        }
    }
}
