using System;
using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using Xunit;

namespace LiteDB.Tests.Issues
{
    public class Issue2874_Tests
    {
        public static IEnumerable<object[]> InvalidWindows()
        {
            yield return new object[] { Array.Empty<byte>(), 0 };
            yield return new object[] { new byte[11], 0 };
            yield return new object[] { new byte[12], 1 };
            yield return new object[] { new byte[23], 12 };
            yield return new object[] { new byte[12], -1 };
            yield return new object[] { new byte[12], int.MaxValue };
        }

        [Theory]
        [MemberData(nameof(InvalidWindows))]
        public void Invalid_byte_windows_are_rejected_as_argument_errors(byte[] bytes, int startIndex)
        {
            Action construct = () => new ObjectId(bytes, startIndex);

            construct.Should().Throw<ArgumentException>()
                .And.Should().NotBeOfType<IndexOutOfRangeException>();
        }

        [Fact]
        public void Every_valid_twelve_byte_window_round_trips_only_its_own_bytes()
        {
            var source = Enumerable.Range(0, 48).Select(i => (byte)(i * 37 + 11)).ToArray();

            foreach (var startIndex in new[] { 0, 1, 7, 18, 36 })
            {
                var expected = source.Skip(startIndex).Take(12).ToArray();
                var actual = new ObjectId(source, startIndex);

                actual.ToByteArray().Should().Equal(expected);
                new ObjectId(actual.ToString()).Should().Be(actual);
            }
        }
    }
}
