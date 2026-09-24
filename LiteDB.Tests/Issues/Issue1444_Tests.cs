using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using FluentAssertions;
using FluentAssertions.Execution;
using Xunit;

namespace LiteDB.Tests.Issues
{
    public class Issue1444_Tests
    {
        public static IEnumerable<object[]> UnsignedTimestampCases()
        {
            yield return TimestampCase(
                new byte[] { 0x00, 0x00, 0x00, 0x00, 0x11, 0x22, 0x33, 0x44, 0x55, 0x66, 0x77, 0x88 },
                "000000001122334455667788",
                0L,
                new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc));
            yield return TimestampCase(
                new byte[] { 0x7f, 0xff, 0xff, 0xff, 0x11, 0x22, 0x33, 0x44, 0x55, 0x66, 0x77, 0x88 },
                "7fffffff1122334455667788",
                2147483647L,
                new DateTime(2038, 1, 19, 3, 14, 7, DateTimeKind.Utc));
            yield return TimestampCase(
                new byte[] { 0x80, 0x00, 0x00, 0x00, 0x11, 0x22, 0x33, 0x44, 0x55, 0x66, 0x77, 0x88 },
                "800000001122334455667788",
                2147483648L,
                new DateTime(2038, 1, 19, 3, 14, 8, DateTimeKind.Utc));
            yield return TimestampCase(
                new byte[] { 0xff, 0xff, 0xff, 0xff, 0x11, 0x22, 0x33, 0x44, 0x55, 0x66, 0x77, 0x88 },
                "ffffffff1122334455667788",
                4294967295L,
                new DateTime(2106, 2, 7, 6, 28, 15, DateTimeKind.Utc));
        }

        public static IEnumerable<object[]> OrderedObjectIdCases()
        {
            yield return new object[]
            {
                "00000000ffffffffffffffff",
                "7fffffff0000000000000000"
            };
            yield return new object[]
            {
                "7fffffffffffffffffffffff",
                "800000000000000000000000"
            };
            yield return new object[]
            {
                "80000000ffffffffffffffff",
                "ffffffff0000000000000000"
            };
            yield return new object[]
            {
                "800000000000000000000000",
                "800000000000000000000001"
            };
        }

        [Fact]
        public void Same_timestamp_order_preserves_unsigned_pid_bytes()
        {
            var earlier = new ObjectId("800000001122337fff667788");
            var later = new ObjectId("800000001122338000667788");
            AssertOrdered(earlier, later);
            earlier.ToByteArray().Should().Equal(new byte[] { 128, 0, 0, 0, 17, 34, 51, 127, 255, 102, 119, 136 });
            later.ToByteArray().Should().Equal(new byte[] { 128, 0, 0, 0, 17, 34, 51, 128, 0, 102, 119, 136 });
        }

        [Theory]
        [MemberData(nameof(UnsignedTimestampCases))]
        public void Timestamp_bits_round_trip_and_creation_time_uses_unsigned_seconds(
            byte[] expectedBytes,
            string expectedHex,
            long expectedTimestamp,
            DateTime expectedCreationTime)
        {
            var fromBytes = new ObjectId(expectedBytes);
            var fromHex = new ObjectId(expectedHex);

            using (new AssertionScope())
            {
                AssertRepresentation(fromBytes, expectedBytes, expectedHex);
                AssertRepresentation(fromHex, expectedBytes, expectedHex);
                fromBytes.Should().Be(fromHex, "both public wire-format constructors describe the same ObjectId");

                AsUnsignedTimestamp(fromBytes).Should().Be(expectedTimestamp);
                AsUnsignedTimestamp(fromHex).Should().Be(expectedTimestamp);
                fromBytes.CreationTime.Should().Be(expectedCreationTime);
                fromHex.CreationTime.Should().Be(expectedCreationTime);
                fromBytes.CreationTime.Kind.Should().Be(DateTimeKind.Utc);
                fromHex.CreationTime.Kind.Should().Be(DateTimeKind.Utc);
            }
        }

        [Theory]
        [MemberData(nameof(OrderedObjectIdCases))]
        public void Comparison_follows_the_unsigned_timestamp_then_the_remaining_bytes(
            string earlierHex,
            string laterHex)
        {
            var earlierFromHex = new ObjectId(earlierHex);
            var laterFromHex = new ObjectId(laterHex);
            var earlierFromBytes = new ObjectId(earlierFromHex.ToByteArray());
            var laterFromBytes = new ObjectId(laterFromHex.ToByteArray());

            using (new AssertionScope())
            {
                AssertOrdered(earlierFromHex, laterFromHex);
                AssertOrdered(earlierFromBytes, laterFromBytes);
            }
        }

        [Fact]
        public void Primary_index_round_trips_and_orders_the_full_unsigned_timestamp_range()
        {
            var expected = new[]
            {
                new ObjectId("000000001122334455667788"),
                new ObjectId("7fffffff1122334455667788"),
                new ObjectId("800000001122334455667788"),
                new ObjectId("ffffffff1122334455667788")
            };

            using (var stream = new MemoryStream())
            using (var database = new LiteDatabase(stream))
            {
                var collection = database.GetCollection("issue1444");

                for (var i = expected.Length - 1; i >= 0; i--)
                {
                    collection.Insert(new BsonDocument { ["_id"] = expected[i] });
                }

                var actual = collection.FindAll().Select(x => x["_id"].AsObjectId).ToArray();

                actual.Should().Equal(expected, "FindAll must follow the ObjectId primary-index order");
                actual.Select(x => x.ToString()).Should().Equal(expected.Select(x => x.ToString()));
            }
        }

        private static object[] TimestampCase(
            byte[] bytes,
            string hex,
            long timestamp,
            DateTime creationTime)
        {
            return new object[] { bytes, hex, timestamp, creationTime };
        }

        private static void AssertRepresentation(ObjectId actual, byte[] expectedBytes, string expectedHex)
        {
            actual.ToByteArray().Should().Equal(expectedBytes);
            actual.ToString().Should().Be(expectedHex);
            new ObjectId(actual.ToByteArray()).Should().Be(actual);
            new ObjectId(actual.ToString()).Should().Be(actual);

            var jsonRoundTrip = JsonSerializer.Deserialize(JsonSerializer.Serialize(actual)).AsObjectId;
            jsonRoundTrip.ToByteArray().Should().Equal(expectedBytes);
            jsonRoundTrip.ToString().Should().Be(expectedHex);
        }

        private static void AssertOrdered(ObjectId earlier, ObjectId later)
        {
            earlier.CompareTo(later).Should().BeLessThan(0);
            later.CompareTo(earlier).Should().BeGreaterThan(0);
            (earlier < later).Should().BeTrue();
            (later > earlier).Should().BeTrue();
            (earlier <= later).Should().BeTrue();
            (later >= earlier).Should().BeTrue();
            ((BsonValue)earlier).CompareTo(later).Should().BeLessThan(0);
            ((BsonValue)later).CompareTo(earlier).Should().BeGreaterThan(0);
        }

        private static long AsUnsignedTimestamp(ObjectId value)
        {
            var timestamp = Convert.ToInt64(value.Timestamp);

            return timestamp < 0 ? timestamp + 4294967296L : timestamp;
        }
    }
}
