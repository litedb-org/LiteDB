using System;
using System.Collections.Generic;
using System.Linq;

using FluentAssertions;
using Xunit;

namespace LiteDB.Tests.Document
{
    public class NumberEquality_Tests
    {
        [Fact]
        public void Equal_numbers_share_hash_codes_and_order_is_a_total_order()
        {
            var random = new Random(2026092312);
            var values = new List<BsonValue>
            {
                0, -0.0, 0.0, 0m, 0.000m, 1, 1L, 1.0, 1m, 1.0m, 0.5, 0.5m, 0.1, 0.1m, 0.10m,
                double.NaN, double.PositiveInfinity, double.NegativeInfinity, double.Epsilon,
                9007199254740992L, 9007199254740992.0, 9007199254740993L, 9007199254740993m,
                long.MaxValue, (double)long.MaxValue, 9223372036854775808m, decimal.MaxValue, (double)decimal.MaxValue,
            };

            for (var i = 0; i < 100; i++)
            {
                var integer = random.Next(-1000, 1000);
                var power = random.Next(0, 4);
                var scaled = integer / (decimal)Math.Pow(10, power);
                values.Add(integer);
                values.Add((long)integer * 1_000_000_007L);
                values.Add((double)scaled);
                values.Add(scaled);
                values.Add(BitConverter.Int64BitsToDouble(((long)random.Next() << 32) | (uint)random.Next()));
            }

            foreach (var left in values)
            {
                foreach (var right in values)
                {
                    var order = left.CompareTo(right);
                    right.CompareTo(left).Should().Be(-order, "{0} vs {1} must be antisymmetric", left, right);
                    if (order == 0)
                    {
                        left.Equals(right).Should().BeTrue();
                        left.GetHashCode().Should().Be(right.GetHashCode(), "{0} ({1}) equals {2} ({3})", left, left.Type, right, right.Type);
                    }
                }
            }

            // Sorting with the comparer must be consistent (transitive) across types.
            var sorted = values.OrderBy(x => x, Comparer<BsonValue>.Create((a, b) => a.CompareTo(b))).ToList();
            for (var i = 0; i < sorted.Count; i++)
                for (var j = i + 1; j < sorted.Count; j++)
                    sorted[i].CompareTo(sorted[j]).Should().BeLessOrEqualTo(0, "{0} sorts before {1}", sorted[i], sorted[j]);
        }

        [Fact]
        public void Double_and_decimal_are_equal_only_when_their_exact_values_are()
        {
            new BsonValue(0.1).Equals(new BsonValue(0.1m)).Should().BeFalse("binary64 0.1 is 0.1000000000000000055511151231257827...");
            new BsonValue(19.99).Equals(new BsonValue(19.99m)).Should().BeFalse();
            new BsonValue(0.5).Equals(new BsonValue(0.5m)).Should().BeTrue();
            new BsonValue(1.0).Equals(new BsonValue(1L)).Should().BeTrue();
            new BsonValue(9007199254740993L).Equals(new BsonValue(9007199254740992.0)).Should().BeFalse();
            new BsonValue(0.1).CompareTo(new BsonValue(0.1m)).Should().Be(1);
        }
    }
}
