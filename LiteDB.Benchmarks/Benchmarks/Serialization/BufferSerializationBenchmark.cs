using BenchmarkDotNet.Attributes;
using LiteDB.Engine;

namespace LiteDB.Benchmarks.Benchmarks.Serialization
{
    [BenchmarkCategory(Constants.Categories.SERIALIZATION)]
    [MemoryDiagnoser]
    public class BufferSerializationBenchmark
    {
        private const int Count = 256;
        private const int Int32Value = 123456789;
        private const double DoubleValue = 123456789.987654321d;
        private const decimal DecimalValue = 123456789.987654321m;

        private BufferSlice _int32Read;
        private BufferSlice[] _int32SplitRead;
        private BufferSlice _int32Write;
        private BufferSlice[] _int32SplitWrite;
        private BufferSlice _doubleRead;
        private BufferSlice[] _doubleSplitRead;
        private BufferSlice _doubleWrite;
        private BufferSlice[] _doubleSplitWrite;
        private BufferSlice _decimalRead;
        private BufferSlice[] _decimalSplitRead;
        private BufferSlice _decimalWrite;
        private BufferSlice[] _decimalSplitWrite;

        [GlobalSetup]
        public void Setup()
        {
            (_int32Read, _int32SplitRead, _int32Write, _int32SplitWrite) =
                CreateBuffers(sizeof(int));
            (_doubleRead, _doubleSplitRead, _doubleWrite, _doubleSplitWrite) =
                CreateBuffers(sizeof(double));
            (_decimalRead, _decimalSplitRead, _decimalWrite, _decimalSplitWrite) =
                CreateBuffers(sizeof(decimal), sizeof(int));

            using (var writer = new BufferWriter(_int32Read))
            {
                for (var i = 0; i < Count; i++) writer.Write(Int32Value);
            }
            using (var writer = new BufferWriter(_doubleRead))
            {
                for (var i = 0; i < Count; i++) writer.Write(DoubleValue);
            }
            using (var writer = new BufferWriter(_decimalRead))
            {
                for (var i = 0; i < Count; i++) writer.Write(DecimalValue);
            }
        }

        [Benchmark]
        public long ReadInt32Contiguous()
        {
            var sum = 0L;
            using var reader = new BufferReader(_int32Read);
            for (var i = 0; i < Count; i++) sum += reader.ReadInt32();
            return sum;
        }

        [Benchmark]
        public long ReadInt32Segmented()
        {
            var sum = 0L;
            using var reader = new BufferReader(_int32SplitRead);
            for (var i = 0; i < Count; i++) sum += reader.ReadInt32();
            return sum;
        }

        [Benchmark]
        public int WriteInt32Contiguous()
        {
            using var writer = new BufferWriter(_int32Write);
            for (var i = 0; i < Count; i++) writer.Write(Int32Value);
            return writer.Position;
        }

        [Benchmark]
        public int WriteInt32Segmented()
        {
            using var writer = new BufferWriter(_int32SplitWrite);
            for (var i = 0; i < Count; i++) writer.Write(Int32Value);
            return writer.Position;
        }

        [Benchmark]
        public double ReadDoubleContiguous()
        {
            var sum = 0d;
            using var reader = new BufferReader(_doubleRead);
            for (var i = 0; i < Count; i++) sum += reader.ReadDouble();
            return sum;
        }

        [Benchmark]
        public double ReadDoubleSegmented()
        {
            var sum = 0d;
            using var reader = new BufferReader(_doubleSplitRead);
            for (var i = 0; i < Count; i++) sum += reader.ReadDouble();
            return sum;
        }

        [Benchmark]
        public int WriteDoubleContiguous()
        {
            using var writer = new BufferWriter(_doubleWrite);
            for (var i = 0; i < Count; i++) writer.Write(DoubleValue);
            return writer.Position;
        }

        [Benchmark]
        public int WriteDoubleSegmented()
        {
            using var writer = new BufferWriter(_doubleSplitWrite);
            for (var i = 0; i < Count; i++) writer.Write(DoubleValue);
            return writer.Position;
        }

        [Benchmark]
        public decimal ReadDecimalContiguous()
        {
            var sum = 0m;
            using var reader = new BufferReader(_decimalRead);
            for (var i = 0; i < Count; i++) sum += reader.ReadDecimal();
            return sum;
        }

        [Benchmark]
        public decimal ReadDecimalSegmented()
        {
            var sum = 0m;
            using var reader = new BufferReader(_decimalSplitRead);
            for (var i = 0; i < Count; i++) sum += reader.ReadDecimal();
            return sum;
        }

        [Benchmark]
        public int WriteDecimalContiguous()
        {
            using var writer = new BufferWriter(_decimalWrite);
            for (var i = 0; i < Count; i++) writer.Write(DecimalValue);
            return writer.Position;
        }

        [Benchmark]
        public int WriteDecimalSegmented()
        {
            using var writer = new BufferWriter(_decimalSplitWrite);
            for (var i = 0; i < Count; i++) writer.Write(DecimalValue);
            return writer.Position;
        }

        private static (BufferSlice Read, BufferSlice[] SplitRead,
            BufferSlice Write, BufferSlice[] SplitWrite) CreateBuffers(
                int valueSize, int componentSize = 0)
        {
            var readBytes = new byte[Count * valueSize];
            var writeBytes = new byte[Count * valueSize];
            var splitWriteBytes = new byte[Count * valueSize];
            componentSize = componentSize == 0 ? valueSize : componentSize;

            return (
                new BufferSlice(readBytes, 0, readBytes.Length),
                CreateCrossingSegments(readBytes, componentSize),
                new BufferSlice(writeBytes, 0, writeBytes.Length),
                CreateCrossingSegments(splitWriteBytes, componentSize));
        }

        private static BufferSlice[] CreateCrossingSegments(byte[] buffer, int componentSize)
        {
            var componentCount = buffer.Length / componentSize;
            var segments = new BufferSlice[componentCount * 2];

            for (var i = 0; i < componentCount; i++)
            {
                var offset = i * componentSize;
                segments[i * 2] = new BufferSlice(buffer, offset, 1);
                segments[i * 2 + 1] = new BufferSlice(buffer, offset + 1, componentSize - 1);
            }

            return segments;
        }
    }
}
