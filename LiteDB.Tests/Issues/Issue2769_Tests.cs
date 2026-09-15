using System;
using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using Xunit;

namespace LiteDB.Tests.Issues
{
    public class Issue2769_Tests
    {
        public enum SignedByte : sbyte { Value = -127 }
        public enum UnsignedByte : byte { Value = 254 }
        public enum SignedShort : short { Value = -32000 }
        public enum UnsignedShort : ushort { Value = 65000 }
        public enum SignedInt : int { Value = -2000000000 }
        public enum UnsignedInt : uint { Value = 4000000000 }
        public enum SignedLong : long { Value = -9007199254740993 }
        public enum UnsignedLong : ulong
        {
            Value = 9007199254740993,
            LowerControl = 0x400000000000002A,
            FirstHighBitValue = 0x8000000000000001,
            DistinctHighBitValue = 0xC00000000000002A,
            Maximum = UInt64.MaxValue
        }

        public class UnsignedLongRow
        {
            public int Id { get; set; }
            public UnsignedLong Value { get; set; }
            public string Marker { get; set; }
        }

        public static IEnumerable<object[]> BackingTypes()
        {
            yield return new object[] { SignedByte.Value, -127L };
            yield return new object[] { UnsignedByte.Value, 254L };
            yield return new object[] { SignedShort.Value, -32000L };
            yield return new object[] { UnsignedShort.Value, 65000L };
            yield return new object[] { SignedInt.Value, -2000000000L };
            yield return new object[] { UnsignedInt.Value, 4000000000L };
            yield return new object[] { SignedLong.Value, -9007199254740993L };
            yield return new object[] { UnsignedLong.Value, 9007199254740993L };
            yield return new object[] { UnsignedLong.FirstHighBitValue, -9223372036854775807L };
            yield return new object[] { UnsignedLong.DistinctHighBitValue, -4611686018427387862L };
            yield return new object[] { UnsignedLong.Maximum, -1L };
        }

        [Theory]
        [MemberData(nameof(BackingTypes))]
        public void Every_enum_backing_type_preserves_its_integer_value(object value, long expected)
        {
            var mapper = new BsonMapper { EnumAsInteger = true };
            var encoded = mapper.Serialize(value.GetType(), value);
            (encoded.IsInt32 || encoded.IsInt64).Should().BeTrue("integer enums must not lose bits through double");
            encoded.AsInt64.Should().Be(expected);

            using var file = new TempFile();
            using (var db = new LiteDatabase(file.Filename))
            {
                db.GetCollection("values").Insert(new BsonDocument { ["_id"] = 1, ["Value"] = encoded });
            }
            using (var db = new LiteDatabase(file.Filename))
            {
                var stored = db.GetCollection("values").FindById(1)["Value"];
                stored.AsInt64.Should().Be(expected);
                mapper.Deserialize(value.GetType(), stored).Should().Be(value);
                // Decode independently authored BSON too; compensating encoder/decoder bugs cannot cancel.
                mapper.Deserialize(value.GetType(), new BsonValue(expected)).Should().Be(value);
            }
        }

        [Fact]
        public void UInt64_enum_high_bit_values_survive_mapping_reopen_and_indexed_queries()
        {
            var mapper = new BsonMapper { EnumAsInteger = true };
            var rows = new[]
            {
                new UnsignedLongRow { Id = 41, Value = UnsignedLong.LowerControl, Marker = "lower-control" },
                new UnsignedLongRow { Id = 13, Value = UnsignedLong.FirstHighBitValue, Marker = "first-high-a" },
                new UnsignedLongRow { Id = 29, Value = UnsignedLong.DistinctHighBitValue, Marker = "distinct-high" },
                new UnsignedLongRow { Id = 7, Value = UnsignedLong.Maximum, Marker = "maximum" },
                new UnsignedLongRow { Id = 43, Value = UnsignedLong.FirstHighBitValue, Marker = "first-high-b" }
            };

            // Hard-coded BSON expectations are independent of the enum encoder and decoder.
            var expectedRawById = new Dictionary<int, long>
            {
                [7] = -1L,
                [13] = -9223372036854775807L,
                [29] = -4611686018427387862L,
                [41] = 4611686018427387946L,
                [43] = -9223372036854775807L
            };

            foreach (var row in rows)
            {
                var mapped = mapper.ToDocument(row);
                mapped["Value"].IsInt64.Should().BeTrue();
                mapped["Value"].AsInt64.Should().Be(expectedRawById[row.Id]);
                mapped["Marker"].AsString.Should().Be(row.Marker);
            }

            using var file = new TempFile();
            using (var db = new LiteDatabase(file.Filename, mapper))
            {
                var collection = db.GetCollection<UnsignedLongRow>("values");
                collection.Insert(rows).Should().Be(5);
                collection.EnsureIndex(x => x.Value).Should().BeTrue();
            }

            using (var db = new LiteDatabase(file.Filename, new BsonMapper { EnumAsInteger = true }))
            {
                var collection = db.GetCollection<UnsignedLongRow>("values");
                var ordered = collection.FindAll().OrderBy(x => x.Id).ToArray();

                ordered.Select(x => x.Id).Should().Equal(7, 13, 29, 41, 43);
                ordered.Select(x => x.Value).Should().Equal(
                    UnsignedLong.Maximum,
                    UnsignedLong.FirstHighBitValue,
                    UnsignedLong.DistinctHighBitValue,
                    UnsignedLong.LowerControl,
                    UnsignedLong.FirstHighBitValue);
                ordered.Select(x => x.Marker).Should().Equal(
                    "maximum", "first-high-a", "distinct-high", "lower-control", "first-high-b");

                AssertTypedQuery(collection, UnsignedLong.LowerControl, 41);
                AssertTypedQuery(collection, UnsignedLong.FirstHighBitValue, 13, 43);
                AssertTypedQuery(collection, UnsignedLong.DistinctHighBitValue, 29);
                AssertTypedQuery(collection, UnsignedLong.Maximum, 7);

                var raw = db.GetCollection("values");
                foreach (var expected in expectedRawById)
                {
                    var stored = raw.FindById(expected.Key);
                    Assert.NotNull(stored);
                    stored["Value"].IsInt64.Should().BeTrue();
                    stored["Value"].AsInt64.Should().Be(expected.Value);
                }

                raw.Find(Query.EQ("Value", -9223372036854775807L))
                    .Select(x => x["_id"].AsInt32).OrderBy(x => x)
                    .Should().Equal(13, 43);
                raw.Find(Query.EQ("Value", 4611686018427387946L))
                    .Select(x => x["_id"].AsInt32)
                    .Should().Equal(new[] { 41 },
                        "the below-Int64-boundary control must remain queryable");
            }
        }

        private static void AssertTypedQuery(
            ILiteCollection<UnsignedLongRow> collection,
            UnsignedLong value,
            params int[] expectedIds)
        {
            collection.Find(x => x.Value == value)
                .Select(x => x.Id).OrderBy(x => x)
                .Should().Equal(expectedIds);
        }
    }
}
