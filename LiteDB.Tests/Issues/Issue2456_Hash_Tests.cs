using System;
using System.Collections.Generic;
using FluentAssertions;
using Xunit;

namespace LiteDB.Tests.Issues;

public class Issue2456_Hash_Tests
{
    [Fact]
    public void Documents_that_differ_in_null_valued_keys_are_distinct()
    {
        var first = new BsonDocument { ["a"] = BsonValue.Null };
        var second = new BsonDocument { ["b"] = BsonValue.Null };
        var third = new BsonDocument { ["a"] = BsonValue.Null, ["x"] = 1 };
        var fourth = new BsonDocument { ["x"] = 1, ["c"] = BsonValue.Null };
        var wrapped = new BsonValue(new Dictionary<string, object> { ["zzz"] = null, ["X"] = 1L });

        first.Equals(second).Should().BeFalse();
        third.Equals(fourth).Should().BeFalse();
        wrapped.Equals(third).Should().BeFalse();
        new HashSet<BsonValue> { first }.Should().NotContain(second);
        new HashSet<BsonValue> { third }.Should().NotContain(fourth);
        new HashSet<BsonValue> { new BsonDocument() }.Should().NotContain(first);
        new HashSet<BsonValue> { new BsonDocument { ["a"] = 1 } }.Should().NotContain(new BsonDocument { ["b"] = 1 });
    }

    [Fact]
    public void Native_and_plain_vectors_should_share_equality_and_hashing()
    {
        BsonValue native = new BsonVector(new[] { 1f, 2f });
        BsonValue plain = new BsonValue((object)new[] { 1f, 2f });

        native.Equals(plain).Should().BeTrue();
        plain.Equals(native).Should().BeTrue();
        native.Equals((object)plain).Should().BeTrue();
        plain.Equals((object)native).Should().BeTrue();
        native.GetHashCode().Should().Be(plain.GetHashCode());
        new HashSet<BsonValue> { native }.Should().Contain(plain);
    }

    [Fact]
    public void Wrapped_collections_should_compare_equal_and_share_hash_codes_with_their_adapters()
    {
        var array = new BsonValue(new[] { "a" });
        var document = new BsonValue(new Dictionary<string, BsonValue> { ["x"] = 1 });

        array.Should().Be(array.AsArray);
        array.GetHashCode().Should().Be(array.AsArray.GetHashCode());
        document.Should().Be(document.AsDocument);
        document.GetHashCode().Should().Be(document.AsDocument.GetHashCode());
    }

    [Fact]
    public void Independently_wrapped_equal_collections_should_support_hash_lookup()
    {
        var firstArray = new BsonValue((object)new BsonValue[] { 1, new byte[] { 2, 3 } });
        var equalArray = new BsonValue((object)new BsonValue[] { 1L, new byte[] { 2, 3 } });
        var firstDocument = new BsonValue(new Dictionary<string, BsonValue>
        {
            ["number"] = 1,
            ["payload"] = new byte[] { 2, 3 }
        });
        var equalDocument = new BsonValue(new Dictionary<string, BsonValue>
        {
            ["PAYLOAD"] = new byte[] { 2, 3 },
            ["NUMBER"] = 1L
        });

        new HashSet<BsonValue> { firstArray }.Should().Contain(equalArray);
        new HashSet<BsonValue> { firstDocument }.Should().Contain(equalDocument);
    }

    [Fact]
    public void Equal_scalars_should_share_hash_codes()
    {
        var utc = new DateTime(2024, 1, 2, 3, 4, 5, DateTimeKind.Utc);

        new HashSet<BsonValue> { new BsonValue(1) }.Should().Contain(new BsonValue(1L));
        new HashSet<BsonValue> { new BsonValue(1.0) }.Should().Contain(new BsonValue(1m));
        new HashSet<BsonValue> { new BsonValue(new byte[] { 1, 2 }) }.Should().Contain(new BsonValue(new byte[] { 1, 2 }));
        new HashSet<BsonValue> { new BsonValue(new float[] { 1f, 2f }) }.Should().Contain(new BsonValue(new float[] { 1f, 2f }));
        new HashSet<BsonValue> { new BsonValue(utc) }.Should().Contain(new BsonValue(utc.ToLocalTime()));
    }

    [Theory]
    [InlineData(7.922816251426434E+28)]   // (double)decimal.MaxValue == 2^96, overflows Convert.ToDecimal
    [InlineData(-7.922816251426434E+28)]
    [InlineData(1E+29)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void Doubles_outside_decimal_range_should_not_throw_from_GetHashCode(double number)
    {
        var scalar = new BsonValue(number);
        var array = new BsonValue((object)new BsonValue[] { number });
        var document = new BsonDocument { ["x"] = number };

        var exception = Record.Exception(() =>
        {
            scalar.GetHashCode();
            array.GetHashCode();
            document.GetHashCode();
        });

        exception.Should().BeNull();
        new HashSet<BsonValue> { scalar }.Should().Contain(new BsonValue(number));
    }
}
