using System;
using FluentAssertions;
using Xunit;

namespace LiteDB.Tests.Document
{
    public class ObjectId_Tests
    {
        [Fact]
        public void ObjectId_BsonValue()
        {
            var oid0 = ObjectId.Empty;
            var oid1 = ObjectId.NewObjectId();
            var oid2 = ObjectId.NewObjectId();
            var oid3 = ObjectId.NewObjectId();

            var c1 = new ObjectId(oid1);
            var c2 = new ObjectId(oid2.ToString());
            var c3 = new ObjectId(oid3.ToByteArray());

            oid0.Should().Be(ObjectId.Empty);
            oid1.Should().Be(c1);
            oid2.Should().Be(c2);
            oid3.Should().Be(c3);

            c2.CompareTo(c3).Should().Be(-1); // 1 < 2
            c1.CompareTo(c2).Should().Be(-1); // 2 < 3

            // serializations
            var joid = JsonSerializer.Serialize(c1);
            var jc1 = JsonSerializer.Deserialize(joid).AsObjectId;

            jc1.Should().Be(c1);
        }

        [Fact]
        public void ObjectId_Equals_Null_Does_Not_Throw()
        {
            var oid0 = default(ObjectId);
            var oid1 = ObjectId.NewObjectId();

            oid1.Equals(null).Should().BeFalse();
            oid1.Equals(oid0).Should().BeFalse();
        }

        [Fact]
        public void ObjectId_GenerateNewId_Creates_Unique_Values()
        {
            var first = ObjectId.GenerateNewId();
            var second = ObjectId.GenerateNewId();

            first.Should().NotBe(ObjectId.Empty);
            second.Should().NotBe(first);
        }

        [Theory]
        [InlineData("507f1f77bcf86cd799439011")]
        [InlineData("507F1F77BCF86CD799439011")]
        public void ObjectId_Parse_Round_Trips_Hex_Strings(string value)
        {
            var parsed = ObjectId.Parse(value);

            parsed.Should().Be(new ObjectId(value));
            parsed.ToString().Should().Be(value.ToLowerInvariant());
            ObjectId.TryParse(value, out var tried).Should().BeTrue();
            tried.Should().Be(parsed);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("507f1f77bcf86cd79943901")]
        [InlineData("507f1f77bcf86cd7994390110")]
        [InlineData("507f1f77bcf86cd79943901g")]
        [InlineData("0x507f1f77bcf86cd799439011")]
        [InlineData("0x7f1f77bcf86cd799439011")]
        public void ObjectId_TryParse_Invalid_Value_Returns_False(string value)
        {
            ObjectId.TryParse(value, out var parsed).Should().BeFalse();
            parsed.Should().BeNull();
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        public void ObjectId_Parse_Null_Or_Empty_Throws_ArgumentNullException(string value)
        {
            Action parse = () => ObjectId.Parse(value);

            parse.Should().Throw<ArgumentNullException>();
        }

        [Theory]
        [InlineData("507f1f77bcf86cd79943901")]
        [InlineData("507f1f77bcf86cd7994390110")]
        [InlineData("0x507f1f77bcf86cd799439011")]
        public void ObjectId_Parse_Incorrect_Length_Throws_ArgumentException(string value)
        {
            Action parse = () => ObjectId.Parse(value);

            parse.Should().Throw<ArgumentException>();
        }

        [Theory]
        [InlineData("507f1f77bcf86cd79943901g")]
        [InlineData("0x7f1f77bcf86cd799439011")]
        public void ObjectId_Parse_Non_Hex_Value_Throws_FormatException(string value)
        {
            Action parse = () => ObjectId.Parse(value);

            parse.Should().Throw<FormatException>();
        }
    }
}
