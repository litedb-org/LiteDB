using System;
using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using Xunit;

namespace LiteDB.Tests.Issues
{
    public class Issue1224_Tests
    {
        public class UnsignedRecord
        {
            [BsonId]
            public ulong Id { get; set; }

            public ulong Value { get; set; }
            public string Marker { get; set; }
        }

        public static IEnumerable<object[]> UnsignedWireValues()
        {
            yield return new object[] { 0UL, 0L, new byte[8] };
            yield return new object[] { 1UL, 1L, new byte[] { 1, 0, 0, 0, 0, 0, 0, 0 } };
            yield return new object[]
            {
                2147483648UL, 2147483648L,
                new byte[] { 0, 0, 0, 0x80, 0, 0, 0, 0 }
            };
            yield return new object[]
            {
                9007199254740993UL,
                9007199254740993L,
                new byte[] { 0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x20, 0x00 }
            };
            yield return new object[]
            {
                9223372036854775807UL,
                9223372036854775807L,
                new byte[] { 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0x7F }
            };
            yield return new object[]
            {
                9223372036854775808UL,
                -9223372036854775808L,
                new byte[] { 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x80 }
            };
            yield return new object[]
            {
                0xFEDCBA9876543210UL,
                -81985529216486896L,
                new byte[] { 0x10, 0x32, 0x54, 0x76, 0x98, 0xBA, 0xDC, 0xFE }
            };
            yield return new object[]
            {
                UInt64.MaxValue,
                -1L,
                new byte[] { 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF }
            };
        }

        [Theory]
        [InlineData(0, 0UL)]
        [InlineData(1, 1UL)]
        [InlineData(Int32.MaxValue, 2147483647UL)]
        [InlineData(-1, UInt64.MaxValue)]
        public void Int32_values_widen_before_reinterpreting_unsigned_bits(int signed, ulong expected)
        {
            ulong actual = new BsonValue(signed);
            actual.Should().Be(expected);
        }

        [Fact]
        public void Non_integer_Bson_values_do_not_silently_coerce_to_unsigned_ids()
        {
            foreach (var value in new BsonValue[] { 1.0, 1m, "1", true })
            {
                Action convert = () => { ulong ignored = value; };
                convert.Should().Throw<InvalidCastException>();
            }
        }

        [Theory]
        [MemberData(nameof(UnsignedWireValues))]
        public void Implicit_UInt64_conversion_preserves_Int64_wire_bits(
            ulong value,
            long signedBits,
            byte[] wireBytes)
        {
            BsonValue encoded = value;

            encoded.Type.Should().Be(BsonType.Int64);
            encoded.RawValue.Should().BeOfType<long>().Which.Should().Be(signedBits);

            var binary = BsonSerializer.Serialize(new BsonDocument { ["u"] = encoded });
            var expectedBinary = new List<byte> { 0x10, 0x00, 0x00, 0x00, 0x12, 0x75, 0x00 };
            expectedBinary.AddRange(wireBytes);
            expectedBinary.Add(0x00);
            binary.Should().Equal(expectedBinary,
                "the BSON type tag and little-endian payload are an independent bit-level oracle");

            var persisted = BsonSerializer.Deserialize(binary)["u"];
            persisted.Type.Should().Be(BsonType.Int64);
            persisted.RawValue.Should().BeOfType<long>().Which.Should().Be(signedBits);
        }

        [Theory]
        [MemberData(nameof(UnsignedWireValues))]
        public void Independently_authored_Int64_bits_convert_back_to_UInt64(
            ulong expected,
            long signedBits,
            byte[] wireBytes)
        {
            BsonValue stored = new BsonValue(signedBits);
            var binary = BsonSerializer.Serialize(new BsonDocument { ["u"] = stored });
            var expectedBinary = new List<byte> { 0x10, 0x00, 0x00, 0x00, 0x12, 0x75, 0x00 };
            expectedBinary.AddRange(wireBytes);
            expectedBinary.Add(0x00);

            stored.Type.Should().Be(BsonType.Int64);
            stored.RawValue.Should().BeOfType<long>().Which.Should().Be(signedBits);
            binary.Should().Equal(expectedBinary,
                "the independently authored Int64 must have the expected BSON type and wire bits");

            ulong actual = stored;

            actual.Should().Be(expected);
        }

        [Fact]
        public void Upper_half_UInt64_values_survive_typed_reopen_and_indexed_queries()
        {
            var input = new[]
            {
                new UnsignedRecord { Id = UInt64.MaxValue, Value = UInt64.MaxValue, Marker = "maximum" },
                new UnsignedRecord { Id = 0xFEDCBA9876543210UL, Value = 0xFEDCBA9876543210UL, Marker = "pattern" },
                new UnsignedRecord { Id = 9223372036854775809UL, Value = 9223372036854775809UL, Marker = "second-upper" },
                new UnsignedRecord { Id = 9223372036854775808UL, Value = 9223372036854775808UL, Marker = "first-upper" }
            };

            using var file = new TempFile();
            using (var db = new LiteDatabase(file.Filename, new BsonMapper()))
            {
                var records = db.GetCollection<UnsignedRecord>("records");
                records.Insert(input).Should().Be(input.Length);
                records.EnsureIndex(x => x.Value).Should().BeTrue();
            }

            using (var db = new LiteDatabase(file.Filename, new BsonMapper()))
            {
                var raw = db.GetCollection("records");
                AssertRawRecord(raw, -9223372036854775808L, "first-upper");
                AssertRawRecord(raw, -9223372036854775807L, "second-upper");
                AssertRawRecord(raw, -81985529216486896L, "pattern");
                AssertRawRecord(raw, -1L, "maximum");

                raw.Find(Query.All(Query.Ascending)).Select(x => x["Marker"].AsString)
                    .Should().Equal("first-upper", "second-upper", "pattern", "maximum");
                raw.Find(Query.All(Query.Descending)).Select(x => x["Marker"].AsString)
                    .Should().Equal("maximum", "pattern", "second-upper", "first-upper");

                var typed = db.GetCollection<UnsignedRecord>("records");
                AssertTypedValueQuery(typed, 9223372036854775808UL, "first-upper");
                AssertTypedValueQuery(typed, 9223372036854775809UL, "second-upper");
                AssertTypedValueQuery(typed, 0xFEDCBA9876543210UL, "pattern");
                AssertTypedValueQuery(typed, UInt64.MaxValue, "maximum");

                AssertTypedIdLookup(typed, 9223372036854775808UL, "first-upper");
                AssertTypedIdLookup(typed, 9223372036854775809UL, "second-upper");
                AssertTypedIdLookup(typed, 0xFEDCBA9876543210UL, "pattern");
                AssertTypedIdLookup(typed, UInt64.MaxValue, "maximum");
            }
        }

        private static void AssertRawRecord(
            ILiteCollection<BsonDocument> records,
            long signedBits,
            string marker)
        {
            var stored = records.FindById(new BsonValue(signedBits));

            Assert.NotNull(stored);
            stored["_id"].Type.Should().Be(BsonType.Int64);
            stored["_id"].RawValue.Should().BeOfType<long>().Which.Should().Be(signedBits);
            stored["Value"].Type.Should().Be(BsonType.Int64);
            stored["Value"].RawValue.Should().BeOfType<long>().Which.Should().Be(signedBits);
            stored["Marker"].AsString.Should().Be(marker);
        }

        private static void AssertTypedValueQuery(
            ILiteCollection<UnsignedRecord> records,
            ulong expected,
            string marker)
        {
            var byValue = records.Find(x => x.Value == expected).ToArray();
            byValue.Should().ContainSingle();
            byValue[0].Id.Should().Be(expected);
            byValue[0].Value.Should().Be(expected);
            byValue[0].Marker.Should().Be(marker);
        }

        private static void AssertTypedIdLookup(
            ILiteCollection<UnsignedRecord> records,
            ulong expected,
            string marker)
        {
            var byId = records.FindById(expected);

            Assert.NotNull(byId);
            byId.Id.Should().Be(expected);
            byId.Value.Should().Be(expected);
            byId.Marker.Should().Be(marker);
        }
    }
}
