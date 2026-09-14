using System.IO;
using System.Linq;
using FluentAssertions;
using LiteDB.Engine;
using Xunit;

namespace LiteDB.Tests.Issues
{
    public class Issue2764_Tests
    {
        [Theory]
        [InlineData(SeekOrigin.Begin, 8192, 8192)]
        [InlineData(SeekOrigin.Current, 0, 8192)]
        [InlineData(SeekOrigin.Current, -8192, 0)]
        [InlineData(SeekOrigin.End, -8192, 8192)]
        [InlineData(SeekOrigin.End, 0, 16384)]
        public void Seek_return_position_and_read_data_use_logical_coordinates(SeekOrigin origin, long offset, long expected)
        {
            using var backing = new MemoryStream();
            using var crypto = new AesStream("regression-password", backing);
            var first = Enumerable.Repeat((byte)0x31, 8192).ToArray();
            var second = Enumerable.Repeat((byte)0x72, 8192).ToArray();
            crypto.Write(first, 0, first.Length);
            crypto.Write(second, 0, second.Length);
            crypto.Flush();
            crypto.Position = 8192;
            var returned = crypto.Seek(offset, origin);
            returned.Should().Be(expected);
            crypto.Position.Should().Be(expected);
            backing.Position.Should().Be(expected + 8192);
            crypto.Length.Should().Be(16384);
            if (expected < crypto.Length)
            {
                var read = new byte[8192];
                crypto.Read(read, 0, read.Length).Should().Be(read.Length);
                read.Should().Equal(expected == 0 ? first : second);
            }
        }
    }
}
