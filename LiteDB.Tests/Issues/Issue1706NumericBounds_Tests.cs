using System;
using System.Linq;
using FluentAssertions;
using Xunit;

namespace LiteDB.Tests.Issues
{
    public class Issue1706NumericBounds_Tests
    {
        [Theory]
        [InlineData(double.MaxValue, 1)]
        [InlineData(double.MinValue, -1)]
        [InlineData(double.PositiveInfinity, 1)]
        [InlineData(double.NegativeInfinity, -1)]
        [InlineData(double.NaN, -1)]
        public void Out_of_decimal_range_doubles_compare_in_both_directions(double value, int order)
        {
            foreach (var other in new BsonValue[] { int.MinValue, 0, long.MaxValue, decimal.MinValue, decimal.MaxValue })
            {
                Math.Sign(new BsonValue(value).CompareTo(other)).Should().Be(order);
                Math.Sign(other.CompareTo(new BsonValue(value))).Should().Be(-order);
            }
            if (value > 0)
                new BsonValue((double)decimal.MaxValue).CompareTo(new BsonValue(decimal.MaxValue)).Should().BePositive();
        }

        [Fact]
        public void Extreme_bounds_have_stable_plans_and_empty_integer_ranges_do_not_throw()
        {
            using var db = new LiteDatabase(":memory:");
            var doubles = db.GetCollection("doubles");
            var integers = db.GetCollection("integers");
            foreach (var id in Enumerable.Range(1, 10))
            {
                doubles.Insert(new BsonDocument { ["_id"] = id, ["key"] = (double)id });
                integers.Insert(new BsonDocument { ["_id"] = id, ["key"] = id });
            }
            doubles.EnsureIndex("key"); integers.EnsureIndex("key");
            var first = doubles.Query().Where(BsonExpression.Create("key >= @0 AND key > @1", 0, double.MaxValue));
            var second = doubles.Query().Where(BsonExpression.Create("key > @1 AND key >= @0", 0, double.MaxValue));
            first.ToArray().Should().BeEmpty(); second.ToArray().Should().BeEmpty();
            first.GetPlan()["index"]["mode"].Should().Be(second.GetPlan()["index"]["mode"]);
            integers.Find(BsonExpression.Create("key < 0 AND key > @0", double.MaxValue)).Should().BeEmpty();
        }
    }
}
