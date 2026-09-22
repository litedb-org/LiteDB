using System;
using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using Xunit;

namespace LiteDB.Tests.Issues
{
    public class Issue2902_Tests
    {
        [Fact]
        public void Mixed_numeric_equality_is_transitive_for_adjacent_doubles()
        {
            var first = new BsonValue(0.1d);
            var adjacent = new BsonValue(BitConverter.Int64BitsToDouble(
                BitConverter.DoubleToInt64Bits(0.1d) + 1));
            var decimalValue = new BsonValue(0.1m);

            var bothEqualTheDecimal = first.Equals(decimalValue) &&
                decimalValue.Equals(adjacent);

            bothEqualTheDecimal.Should().BeFalse(
                "two unequal doubles cannot both equal the same decimal value");
            first.Equals(adjacent).Should().BeFalse();
        }

        [Fact]
        public void Smallest_positive_double_is_not_equal_to_decimal_zero()
        {
            new BsonValue(double.Epsilon).Equals(new BsonValue(0m)).Should().BeFalse();
        }

        [Fact]
        public void Exact_numeric_boundaries_have_their_mathematical_order()
        {
            new BsonValue(0.1d).CompareTo(new BsonValue(0.1m)).Should().Be(1);
            new BsonValue(-0.1d).CompareTo(new BsonValue(-0.1m)).Should().Be(-1);
            new BsonValue(9007199254740993L).CompareTo(new BsonValue(9007199254740992d)).Should().Be(1);
            new BsonValue(long.MaxValue).CompareTo(new BsonValue(9223372036854775808d)).Should().Be(-1);
            new BsonValue(decimal.MaxValue).CompareTo(new BsonValue((double)decimal.MaxValue)).Should().Be(-1);
            new BsonValue(double.Epsilon).CompareTo(new BsonValue(0m)).Should().Be(1);
            new BsonValue(-double.Epsilon).CompareTo(new BsonValue(0m)).Should().Be(-1);
            new BsonValue(0.5d).CompareTo(new BsonValue(0.50m)).Should().Be(0);
        }

        [Fact]
        public void Mixed_values_form_a_transitive_order_and_equal_values_have_equal_hashes()
        {
            var values = new BsonValue[]
            {
                double.NaN, BitConverter.Int64BitsToDouble(0x7ff8000000000001),
                double.NegativeInfinity, -double.MaxValue, decimal.MinValue, long.MinValue,
                -0.1d, -0.1m, -double.Epsilon, -0d, 0m, 0, 0L,
                double.Epsilon, 0.1m, 0.1d,
                BitConverter.Int64BitsToDouble(BitConverter.DoubleToInt64Bits(0.1d) + 1),
                0.5d, 0.50m, 1, 1L, 1d, 1m, 9007199254740992d, 9007199254740993L,
                long.MaxValue, decimal.MaxValue, (double)decimal.MaxValue, double.MaxValue, double.PositiveInfinity
            };
            foreach (var a in values)
            foreach (var b in values)
            {
                Math.Sign(a.CompareTo(b)).Should().Be(-Math.Sign(b.CompareTo(a)));
                if (a.Equals(b)) a.GetHashCode().Should().Be(b.GetHashCode());
                foreach (var c in values)
                    if (a.CompareTo(b) <= 0 && b.CompareTo(c) <= 0) a.CompareTo(c).Should().BeLessOrEqualTo(0);
            }
            new HashSet<BsonValue> { 0.1d, 0.1m, double.Epsilon, 0m }.Should().HaveCount(4);
        }

        [Fact]
        public void Numeric_hashes_preserve_high_precision_decimal_diversity()
        {
            var hashes = Enumerable.Range(0, 1000)
                .Select(i => new BsonValue(1m + i * 0.0000000000000000000000000001m).GetHashCode())
                .Distinct().Count();
            hashes.Should().BeGreaterThan(900);
        }

        [Theory]
        [InlineData(0L)]
        [InlineData(1L)]
        [InlineData(-1L)]
        [InlineData((long)int.MinValue)]
        [InlineData((long)int.MaxValue)]
        [InlineData(long.MinValue)]
        [InlineData(long.MaxValue)]
        [InlineData(9007199254740993L)]
        public void Integral_hash_fast_path_matches_equal_decimal_values(long value)
        {
            var integer = new BsonValue(value);
            var decimalValue = new BsonValue((decimal)value);
            integer.GetHashCode().Should().Be(decimalValue.GetHashCode());
            if (value >= int.MinValue && value <= int.MaxValue)
                new BsonValue((int)value).GetHashCode().Should().Be(decimalValue.GetHashCode());
        }

        [Fact]
        public void Distinct_mixed_numeric_unique_index_keys_survive_reopen()
        {
            using var file = new TempFile();
            var keys = new BsonValue[] { 0m, double.Epsilon, 0.1m, 0.1d,
                BitConverter.Int64BitsToDouble(BitConverter.DoubleToInt64Bits(0.1d) + 1),
                9007199254740992d, 9007199254740993L };
            using (var db = new LiteDatabase(file.Filename))
            {
                var rows = db.GetCollection("rows");
                rows.EnsureIndex("key", true);
                foreach (var i in Enumerable.Range(0, keys.Length).Reverse())
                    rows.Insert(new BsonDocument { ["_id"] = i + 1, ["key"] = keys[i] });
            }
            using var reopened = new LiteDatabase(file.Filename);
            var stored = reopened.GetCollection("rows");
            // Typed parameters test the persisted comparer without a text/number conversion.
            for (var i = 0; i < keys.Length; i++)
                stored.Find(BsonExpression.Create("key = @0", keys[i])).Single()["_id"].AsInt32.Should().Be(i + 1);
            stored.Find(Query.All("key")).Select(row => row["_id"].AsInt32).Should().Equal(Enumerable.Range(1, keys.Length));
        }
    }
}
