using System.IO;
using System.Threading;
using FluentAssertions;
using LiteDB.Engine;
using Xunit;
using static LiteDB.Constants;

namespace LiteDB.Internals
{
    public class StreamSynchronization_Tests
    {
        [Theory]
        [InlineData(null)]
        [InlineData("secret")]
        public void TruncateAndTrim_UseTheSameMonitorAsReaders(string password)
        {
            var stream = new MonitorCheckingStream();
            using var factory = new StreamFactory(stream, password, true);
            using var writer = factory.GetStream(true, false);
            using var reader = factory.GetStream(false, false);
            var bytes = new byte[PAGE_SIZE];
            bytes[0] = 42;
            writer.Write(bytes, 0, bytes.Length);
            writer.Write(bytes, 0, bytes.Length);
            writer.Flush();

            writer.SetLength(PAGE_SIZE);
            var capacityBeforeTrim = stream.Capacity;
            factory.TrimCapacity(writer);

            stream.Capacity.Should().BeLessThan(capacityBeforeTrim);
            stream.Capacity.Should().Be((int)stream.Length);
            var result = new byte[PAGE_SIZE];
            reader.Read(result, 0, result.Length).Should().Be(PAGE_SIZE);
            result.Should().Equal(bytes);
        }

        private sealed class MonitorCheckingStream : MemoryStream
        {
            public override void SetLength(long value)
            {
                Monitor.IsEntered(this).Should().BeTrue("truncation must exclude concurrent readers");
                base.SetLength(value);
            }

            public override int Capacity
            {
                get => base.Capacity;
                set
                {
                    Monitor.IsEntered(this).Should().BeTrue("buffer replacement must exclude concurrent readers");
                    base.Capacity = value;
                }
            }

            public override int Read(byte[] buffer, int offset, int count)
            {
                Monitor.IsEntered(this).Should().BeTrue("readers must use the same base-stream monitor");
                return base.Read(buffer, offset, count);
            }
        }
    }
}
