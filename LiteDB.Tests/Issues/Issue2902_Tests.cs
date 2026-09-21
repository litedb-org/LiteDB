using System;

using FluentAssertions;

using Xunit;

namespace LiteDB.Tests.Issues;

public class Issue2902_Tests
{
    [Fact]
    public void Mixed_Numeric_Equality_Is_Transitive()
    {
        var first = new BsonValue(0.1d);
        var second = new BsonValue(BitConverter.Int64BitsToDouble(BitConverter.DoubleToInt64Bits(0.1d) + 1));
        var decimalValue = new BsonValue(0.1m);

        first.Equals(second).Should().BeFalse();
        first.Equals(decimalValue).Should().BeFalse();
        second.Equals(decimalValue).Should().BeFalse();
    }

    [Fact]
    public void Smallest_Double_Is_Greater_Than_Zero_Decimal()
    {
        new BsonValue(double.Epsilon).CompareTo(new BsonValue(0m)).Should().BePositive();
        new BsonValue(0m).CompareTo(new BsonValue(double.Epsilon)).Should().BeNegative();
        new BsonValue(-double.Epsilon).CompareTo(new BsonValue(0m)).Should().BeNegative();
    }

    [Fact]
    public void Integers_Beyond_Double_Precision_Keep_Their_Order()
    {
        var exact = new BsonValue(9007199254740993L);
        var rounded = new BsonValue(9007199254740992d);

        exact.CompareTo(rounded).Should().BePositive();
        rounded.CompareTo(exact).Should().BeNegative();
        new BsonValue(9007199254740992L).CompareTo(rounded).Should().Be(0);
    }

    [Fact]
    public void Equal_Numbers_Share_A_Hash_Code()
    {
        AssertEqualWithSameHash(new BsonValue(3722885709126892.5d), new BsonValue(3722885709126892.5m));
        AssertEqualWithSameHash(new BsonValue(9007199254740993m), new BsonValue(9007199254740993.00m));
        AssertEqualWithSameHash(new BsonValue(9007199254740993L), new BsonValue(9007199254740993.00m));
        AssertEqualWithSameHash(new BsonValue(9007199254740992L), new BsonValue(9007199254740992d));
        AssertEqualWithSameHash(new BsonValue(1), new BsonValue(1.0d));
        AssertEqualWithSameHash(new BsonValue(1L), new BsonValue(1m));
        AssertEqualWithSameHash(new BsonValue(0d), new BsonValue(-0.0d));
        AssertEqualWithSameHash(new BsonValue(0.5d), new BsonValue(0.5m));
    }

    [Fact]
    public void Numeric_Boundaries_Compare_Exactly()
    {
        new BsonValue(long.MaxValue).CompareTo(new BsonValue(9223372036854775808d)).Should().BeNegative();
        new BsonValue(long.MinValue).CompareTo(new BsonValue(-9223372036854775808d)).Should().Be(0);
        new BsonValue(decimal.MaxValue).CompareTo(new BsonValue((double)decimal.MaxValue)).Should().BeNegative();
        new BsonValue(decimal.MinValue).CompareTo(new BsonValue((double)decimal.MinValue)).Should().BePositive();
        new BsonValue(1m).CompareTo(new BsonValue(double.NaN)).Should().BePositive();
        new BsonValue(1L).CompareTo(new BsonValue(double.NaN)).Should().BePositive();
        new BsonValue(1m).CompareTo(new BsonValue(double.PositiveInfinity)).Should().BeNegative();
        new BsonValue(1m).CompareTo(new BsonValue(double.NegativeInfinity)).Should().BePositive();
        new BsonValue(0.0000000000000000000000000001m).CompareTo(new BsonValue(0d)).Should().BePositive();
    }

    private static void AssertEqualWithSameHash(BsonValue left, BsonValue right)
    {
        left.CompareTo(right).Should().Be(0, "{0} and {1} hold the same value", left, right);
        left.Equals(right).Should().BeTrue();
        left.GetHashCode().Should().Be(right.GetHashCode(), "{0} and {1} are equal", left, right);
    }

    [Fact]
    public void Mixed_Numeric_Ordering_Is_Consistent()
    {
        BsonValue[] values =
        [
            new BsonValue(0.1d),
            new BsonValue(0.1m),
            new BsonValue(1),
            new BsonValue(1L),
            new BsonValue(1m),
            new BsonValue(1.0d),
            new BsonValue(double.Epsilon),
            new BsonValue(0m),
            new BsonValue(9007199254740993L),
            new BsonValue(9007199254740992d),
            new BsonValue(decimal.MaxValue),
            new BsonValue(double.MaxValue)
        ];

        foreach (var x in values)
        {
            x.CompareTo(x).Should().Be(0);

            foreach (var y in values)
            {
                Math.Sign(x.CompareTo(y)).Should().Be(-Math.Sign(y.CompareTo(x)), "{0} vs {1}", x, y);

                foreach (var z in values)
                {
                    var xy = Math.Sign(x.CompareTo(y));
                    var yz = Math.Sign(y.CompareTo(z));

                    if (xy == 0)
                    {
                        x.GetHashCode().Should().Be(y.GetHashCode(), "{0} and {1} are equal", x, y);
                        Math.Sign(x.CompareTo(z)).Should().Be(yz, "{0}, {1}, {2}", x, y, z);
                    }
                    else if (yz == 0 || yz == xy)
                    {
                        Math.Sign(x.CompareTo(z)).Should().Be(xy, "{0}, {1}, {2}", x, y, z);
                    }
                }
            }
        }
    }
}
