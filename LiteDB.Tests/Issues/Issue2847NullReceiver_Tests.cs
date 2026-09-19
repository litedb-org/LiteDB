using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using FluentAssertions;
using Xunit;

namespace LiteDB.Tests.Issues
{
    public class Issue2847NullReceiver_Tests
    {
        public class Row { public int Id { get; set; } public string Name { get; set; } }

        public static IEnumerable<object[]> Predicates()
        {
            const StringComparison Mode = StringComparison.OrdinalIgnoreCase;
            yield return new object[] { (Expression<Func<Row, bool>>)(row => row.Name.Equals("X1", Mode)) };
            yield return new object[] { (Expression<Func<Row, bool>>)(row => row.Name.StartsWith("X", Mode)) };
            yield return new object[] { (Expression<Func<Row, bool>>)(row => row.Name.EndsWith("1", Mode)) };
            yield return new object[] { (Expression<Func<Row, bool>>)(row => row.Name.IndexOf("X1", Mode) >= 0) };
            yield return new object[] { (Expression<Func<Row, bool>>)(row => row.Name.IndexOf("X1", 0, Mode) >= 0) };
            yield return new object[] { (Expression<Func<Row, bool>>)(row => row.Name.IndexOf("X1", 0, 2, Mode) >= 0) };
#if !NETFRAMEWORK
            yield return new object[] { (Expression<Func<Row, bool>>)(row => row.Name.Contains("X1", Mode)) };
#endif
        }

        [Theory]
        [MemberData(nameof(Predicates))]
        public void Null_missing_and_non_string_receivers_do_not_match_instead_of_aborting_the_query(Expression<Func<Row, bool>> predicate)
        {
            using var db = new LiteDatabase(":memory:");
            db.GetCollection("rows").Insert(new[]
            {
                new BsonDocument { ["_id"] = 1, ["Name"] = BsonValue.Null },
                new BsonDocument { ["_id"] = 2 },
                new BsonDocument { ["_id"] = 3, ["Name"] = 5 },
                new BsonDocument { ["_id"] = 4, ["Name"] = "x1" }
            });
            var rows = db.GetCollection<Row>("rows");

            rows.Count(predicate).Should().Be(1);
            rows.DeleteMany(predicate).Should().Be(1);
            rows.Count().Should().Be(3);
        }

        [Theory]
        [InlineData("STRING_EQUALS_INSTANCE")]
        [InlineData("STRING_STARTSWITH")]
        [InlineData("STRING_ENDSWITH")]
        [InlineData("STRING_CONTAINS")]
        public void Explicit_predicates_are_false_for_null_and_non_string_receivers(string function)
        {
            BsonExpression.Create(function + "(null, 'x', 'Ordinal')").ExecuteScalar().Should().Be(new BsonValue(false));
            BsonExpression.Create(function + "(5, 'x', 'Ordinal')").ExecuteScalar().Should().Be(new BsonValue(false));
            BsonExpression.Create(function + "(null, null, 'Ordinal')").ExecuteScalar().Should().Be(new BsonValue(false));
        }

        [Fact]
        public void Explicit_IndexOf_is_null_for_null_and_non_string_receivers_like_INDEXOF()
        {
            BsonExpression.Create("INDEXOF(null, 'x')").ExecuteScalar().Should().Be(BsonValue.Null);
            BsonExpression.Create("STRING_INDEXOF(null, 'x', 'Ordinal')").ExecuteScalar().Should().Be(BsonValue.Null);
            BsonExpression.Create("STRING_INDEXOF(5, 'x', 0, 'Ordinal')").ExecuteScalar().Should().Be(BsonValue.Null);
            BsonExpression.Create("STRING_INDEXOF(null, 'x', 0, 1, 'Ordinal')").ExecuteScalar().Should().Be(BsonValue.Null);
        }
    }
}
