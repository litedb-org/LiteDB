using System;
using System.Collections.Generic;
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
        public enum UnsignedLong : ulong { Value = 9007199254740993 }

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
    }
}
