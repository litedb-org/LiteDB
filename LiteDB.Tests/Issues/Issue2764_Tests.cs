using System;
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
        [InlineData(SeekOrigin.Begin, -1)]
        [InlineData(SeekOrigin.Current, -1)]
        [InlineData(SeekOrigin.End, -8193)]
        public void Seek_before_logical_start_preserves_position_and_encryption_header(SeekOrigin origin, long offset)
        {
            using var backing = new MemoryStream();
            using var crypto = new AesStream("regression-password", backing);
            crypto.Write(new byte[8192], 0, 8192);
            crypto.Position = 0;
            var bytes = backing.ToArray();

            Action seek = () => crypto.Seek(offset, origin);
            seek.Should().Throw<IOException>();
            crypto.Position.Should().Be(0);
            backing.Position.Should().Be(8192);
            backing.ToArray().Should().Equal(bytes);
        }

        [Fact]
        public void Invalid_origin_and_overflow_do_not_move_the_stream()
        {
            using var backing = new MemoryStream();
            using var crypto = new AesStream("regression-password", backing);
            Action invalid = () => crypto.Seek(0, (SeekOrigin)123);
            invalid.Should().Throw<ArgumentException>();
            Action overflow = () => crypto.Seek(long.MaxValue, SeekOrigin.Begin);
            overflow.Should().Throw<OverflowException>();
            crypto.Position.Should().Be(0);
            backing.Position.Should().Be(8192);
        }

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
