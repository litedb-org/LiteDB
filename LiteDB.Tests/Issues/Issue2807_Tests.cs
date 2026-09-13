using System;
using System.Linq;
using FluentAssertions.Execution;
using FluentAssertions;
using Xunit;

namespace LiteDB.Tests.Issues
{
    public class Issue2807_Tests
    {
        [Theory]
        [InlineData("EQ")]
        [InlineData("LT")]
        [InlineData("LTE")]
        [InlineData("GT")]
        [InlineData("GTE")]
        [InlineData("Between")]
        [InlineData("StartsWith")]
        [InlineData("EndsWith")]
        [InlineData("Contains")]
        [InlineData("Not")]
        [InlineData("In")]
        public void Helper_reuses_source_but_keeps_independent_parameter_values(string operation)
        {
            var values = new[] { "a", "ab", "b", "ba", "c" };
            var documents = values.Select(x => new BsonDocument { ["value"] = x }).ToArray();
            var first = Create(operation, "a");
            var second = Create(operation, "b");
            using (new AssertionScope())
            {
                first.Source.Should().Be(second.Source, "different values must not require recompilation");
                first.Parameters.Count.Should().BeGreaterThan(0);
                second.Parameters.Count.Should().BeGreaterThan(0);
                foreach (var expression in new[] { first, second, first })
                {
                    var value = ReferenceEquals(expression, first) ? "a" : "b";
                    var expected = values.Where(x => Matches(operation, x, value));
                    documents.Where(x => expression.ExecuteScalar(x).AsBoolean)
                        .Select(x => x["value"].AsString).Should().Equal(expected);
                }
            }
        }

        private static BsonExpression Create(string op, string value)
        {
            switch (op)
            {
                case "EQ": return Query.EQ("value", value);
                case "LT": return Query.LT("value", value);
                case "LTE": return Query.LTE("value", value);
                case "GT": return Query.GT("value", value);
                case "GTE": return Query.GTE("value", value);
                case "Between": return Query.Between("value", value, value + "z");
                case "StartsWith": return Query.StartsWith("value", value);
                case "EndsWith": return Query.EndsWith("value", value);
                case "Contains": return Query.Contains("value", value);
                case "Not": return Query.Not("value", value);
                case "In": return Query.In("value", value, value + "a");
                default: throw new ArgumentException(op);
            }
        }

        private static bool Matches(string op, string x, string value)
        {
            var comparison = string.CompareOrdinal(x, value);
            switch (op)
            {
                case "EQ": return comparison == 0;
                case "LT": return comparison < 0;
                case "LTE": return comparison <= 0;
                case "GT": return comparison > 0;
                case "GTE": return comparison >= 0;
                case "Between": return comparison >= 0 && string.CompareOrdinal(x, value + "z") <= 0;
                case "StartsWith": return x.StartsWith(value, StringComparison.Ordinal);
                case "EndsWith": return x.EndsWith(value, StringComparison.Ordinal);
                case "Contains": return x.Contains(value);
                case "Not": return comparison != 0;
                case "In": return x == value || x == value + "a";
                default: throw new ArgumentException(op);
            }
        }
    }
}
