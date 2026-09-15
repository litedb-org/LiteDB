using System;
using System.Globalization;
using System.Linq;

using FluentAssertions;
using Xunit;

namespace LiteDB.Tests.Audit2026
{
    public class ExpressionAuditRegression_Tests
    {
        [Theory]
        [InlineData("10 - 5 + 3", 8)]
        [InlineData("10 * 4 % 3", 1)]
        [Trait("Category", "AuditBehavior")]
        public void H11_operators_with_equal_precedence_are_left_associative(string expression, int expected)
        {
            BsonExpression.Create(expression).ExecuteScalar().AsInt32.Should().Be(expected);
        }

        [Theory]
        [InlineData("Hawaii", "Hawai", false)]
        [InlineData("Smith", "%_mith", true)]
        [InlineData("a", "%_", true)]
        [Trait("Category", "AuditBehavior")]
        public void H12_H13_sql_like_obeys_full_match_wildcards(string value, string pattern, bool expected)
        {
            value.SqlLike(pattern, Collation.Binary).Should().Be(expected);
        }

        [Fact]
        [Trait("Category", "AuditBehavior")]
        public void H14_trailing_single_character_wildcard_requires_validation()
        {
            "abc_".SqlLikeStartsWith(out var hasMore).Should().Be("abc");
            hasMore.Should().BeTrue();
        }

        [Fact]
        [Trait("Category", "AuditBehavior")]
        public void H16_document_comparison_is_antisymmetric()
        {
            var left = new BsonDocument { ["a"] = 1 };
            var right = new BsonDocument { ["b"] = 1 };

            Math.Sign(left.CompareTo(right)).Should().Be(-Math.Sign(right.CompareTo(left)));
        }

        [Fact]
        [Trait("Category", "AuditBehavior")]
        public void H29_sum_and_average_do_not_overflow_int32()
        {
            var values = new BsonArray { 2_000_000_000, 2_000_000_000, 2_000_000_000 };

            BsonExpressionMethods.SUM(values).AsDecimal.Should().Be(6_000_000_000m);
            BsonExpressionMethods.AVG(values).AsDecimal.Should().Be(2_000_000_000m);
        }

        [Theory]
        [InlineData("SUBSTRING('abc', 10)")]
        [InlineData("SUBSTRING('abc', -10, 1)")]
        [Trait("Category", "AuditBehavior")]
        public void H54_invalid_string_ranges_return_bson_null(string expression)
        {
            BsonExpression.Create(expression).ExecuteScalar().Should().Be(BsonValue.Null);
        }

        [Fact]
        [Trait("Category", "AuditBehavior")]
        public void H55_empty_first_and_last_return_bson_null()
        {
            BsonExpressionMethods.FIRST(Array.Empty<BsonValue>()).Should().Be(BsonValue.Null);
            BsonExpressionMethods.LAST(Array.Empty<BsonValue>()).Should().Be(BsonValue.Null);
        }

        [Fact]
        [Trait("Category", "AuditBehavior")]
        public void M111_uint64_round_trips_through_bson_value()
        {
            BsonValue value = UInt64.MaxValue;
            ((UInt64)value).Should().Be(UInt64.MaxValue);
        }

        [Theory]
        [InlineData(Int32.MinValue)]
        [InlineData(Int64.MinValue)]
        [Trait("Category", "AuditBehavior")]
        public void M120_abs_edge_values_do_not_leak_framework_exceptions(object raw)
        {
            Action execute = () => BsonExpressionMethods.ABS(new BsonValue(raw));
            execute.Should().NotThrow<OverflowException>();
        }

        [Fact]
        [Trait("Category", "AuditBehavior")]
        public void M120_round_invalid_digits_returns_bson_null()
        {
            BsonExpressionMethods.ROUND(1.2d, 100).Should().Be(BsonValue.Null);
        }

        [Fact]
        [Trait("Category", "AuditBehavior")]
        public void M119_match_non_string_returns_bson_null()
        {
            BsonExpressionMethods.MATCH(1, "x", 0).Should().Be(BsonValue.Null);
        }

        [Fact]
        [Trait("Category", "AuditBehavior")]
        public void M145_collation_hash_matches_collation_equality()
        {
            var collation = new Collation(9, CompareOptions.IgnoreCase);
            var lower = new BsonValue("a");
            var upper = new BsonValue("A");

            collation.Equals(lower, upper).Should().BeTrue();
            collation.GetHashCode(lower).Should().Be(collation.GetHashCode(upper));
        }

        [Fact]
        [Trait("Category", "AuditBehavior")]
        public void M148_array_index_before_start_returns_bson_null()
        {
            var expression = BsonExpression.Create("$.items[-3]");
            var root = new BsonDocument { ["items"] = new BsonArray { 1, 2 } };

            expression.ExecuteScalar(root).Should().Be(BsonValue.Null);
        }

        [Fact]
        [Trait("Category", "AuditBehavior")]
        public void M118_unknown_json_escape_is_rejected()
        {
            Action deserialize = () => JsonSerializer.Deserialize("\"\\q\"");
            deserialize.Should().Throw<LiteException>();
        }

        [Fact]
        [Trait("Category", "AuditBehavior")]
        public void M123_wide_date_difference_does_not_leak_overflow()
        {
            Action execute = () => BsonExpression.Create(
                "DATEDIFF('second', DATEADD('year', -100, NOW()), NOW())").ExecuteScalar();

            execute.Should().NotThrow<OverflowException>();
        }
    }
}
