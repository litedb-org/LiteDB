using System;
using System.IO;
using Xunit;

namespace LiteDB.Tests.Issues
{
    public class Issue2052DateBounds_Tests
    {
        [Theory]
        [InlineData(-62135596800001L)]
        [InlineData(253402300800001L)]
        public void First_out_of_range_millisecond_has_a_structured_format_error(long milliseconds)
        {
            var bytes = Document(milliseconds);
            var error = Assert.Throws<LiteException>(() => BsonSerializer.Deserialize(bytes, utcDate: true));
            Assert.Equal(LiteException.INVALID_FORMAT, error.ErrorCode);
            Assert.Contains("DateTime", error.Message);
        }

        [Theory]
        [InlineData(-62135596800000L)]
        [InlineData(-62135596799999L)]
        [InlineData(253402300799999L)]
        [InlineData(253402300800000L)]
        [InlineData(0L)]
        public void Valid_boundaries_and_legacy_sentinels_still_decode(long milliseconds)
        {
            var expected = milliseconds == 253402300800000L ? DateTime.MaxValue :
                milliseconds == -62135596800000L ? DateTime.MinValue : BsonValue.UnixEpoch.AddMilliseconds(milliseconds);
            var actual = BsonSerializer.Deserialize(Document(milliseconds), utcDate: true)["date"].AsDateTime;
            Assert.Equal(expected, actual);
        }

        private static byte[] Document(long milliseconds)
        {
            using var stream = new MemoryStream();
            using var writer = new BinaryWriter(stream);
            writer.Write(19);
            writer.Write((byte)0x09);
            writer.Write(new byte[] { (byte)'d', (byte)'a', (byte)'t', (byte)'e', 0 });
            writer.Write(milliseconds);
            writer.Write((byte)0);
            return stream.ToArray();
        }
    }
}
