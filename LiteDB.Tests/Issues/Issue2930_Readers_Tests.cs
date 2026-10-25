using System;
using System.IO;
using FluentAssertions;
using LiteDB.Engine;
using Xunit;

namespace LiteDB.Tests.Issues
{
    public class Issue2930_Readers_Tests
    {
        private const long MIN_MS = -62135596800000L;
        private const long MAX_MS = 253402300800000L;

        private static readonly long _maxTicks = DateTime.MaxValue.Ticks;

        [Theory]
        [InlineData(MIN_MS - 1, false)]
        [InlineData(long.MinValue, false)]
        [InlineData(MAX_MS + 1, true)]
        [InlineData(long.MaxValue, true)]
        public void Legacy_BSON_reader_clamps_out_of_range_milliseconds(long milliseconds, bool high)
        {
            var document = new BsonReader(utcDate: true).Deserialize(BsonDate(milliseconds));

            document["date"].AsDateTime.Should().Be(high ? DateTime.MaxValue : DateTime.MinValue);
        }

        [Theory]
        [InlineData(-1L, false)]
        [InlineData(long.MinValue, false)]
        [InlineData(1L, true)]
        [InlineData(long.MaxValue - 3155378975999999999L, true)]
        public void Index_key_readers_clamp_out_of_range_ticks(long offset, bool high)
        {
            var ticks = high ? _maxTicks + offset : offset;
            var expected = high ? DateTime.MaxValue : DateTime.MinValue;
            var expectedLocal = new DateTime(expected.Ticks, DateTimeKind.Utc).ToLocalTime();
            var key = IndexKey(ticks);

            new BufferSlice(key, 0, key.Length).ReadIndexKey(0).AsDateTime.Should().Be(expected);
            new BufferReader(key).ReadIndexKey().AsDateTime.Should().Be(expected);
            new BufferReader(key, utcDate: true).ReadIndexKey().AsDateTime.Should().Be(expectedLocal);
            new ByteReader(key).ReadBsonValue(0).AsDateTime.Should().Be(expectedLocal);
        }

        [Theory]
        [InlineData(0L)]
        [InlineData(10000L)]
        [InlineData(637634088000000000L)]
        [InlineData(3155378975999990000L)]
        [InlineData(3155378975999999999L)]
        public void Index_key_readers_keep_valid_ticks_and_kind(long ticks)
        {
            var utc = new DateTime(ticks, DateTimeKind.Utc);
            var key = IndexKey(ticks);

            var fromSlice = new BufferSlice(key, 0, key.Length).ReadIndexKey(0).AsDateTime;
            var fromReader = new BufferReader(key).ReadIndexKey().AsDateTime;
            var fromLocalReader = new BufferReader(key, utcDate: true).ReadIndexKey().AsDateTime;
            var fromLegacy = new ByteReader(key).ReadBsonValue(0).AsDateTime;

            fromSlice.Ticks.Should().Be(ticks);
            fromReader.Ticks.Should().Be(ticks);
            fromReader.Kind.Should().Be(DateTimeKind.Utc);
            fromLocalReader.Should().Be(utc.ToLocalTime());
            fromLocalReader.Kind.Should().Be(DateTimeKind.Local);
            fromLegacy.Should().Be(utc.ToLocalTime());
            fromLegacy.Kind.Should().Be(DateTimeKind.Local);
        }

        // The BSON sentinels (#19) and the clamped values share one result: exactly DateTime.MinValue / MaxValue, whether
        // dates are read as UTC or as local time. Shifting them to local time would move MinValue east of UTC.
        [Theory]
        [InlineData(MIN_MS, false)]
        [InlineData(MIN_MS - 1, false)]
        [InlineData(long.MinValue, false)]
        [InlineData(MAX_MS, true)]
        [InlineData(MAX_MS + 1, true)]
        [InlineData(long.MaxValue, true)]
        public void Document_reader_returns_the_exact_bound_for_sentinel_and_clamped_milliseconds(long milliseconds, bool high)
        {
            var expected = high ? DateTime.MaxValue : DateTime.MinValue;

            foreach (var utcDate in new[] { false, true })
            {
                var date = new BufferReader(BsonDate(milliseconds), utcDate).ReadDocument().GetValue()["date"].AsDateTime;

                date.Ticks.Should().Be(expected.Ticks);
                date.Kind.Should().Be(DateTimeKind.Unspecified);
            }
        }

        private static byte[] IndexKey(long ticks)
        {
            var key = new byte[9];

            key[0] = (byte)BsonType.DateTime;
            BitConverter.GetBytes(ticks).CopyTo(key, 1);

            return key;
        }

        private static byte[] BsonDate(long milliseconds)
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
