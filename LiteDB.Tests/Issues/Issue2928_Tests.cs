using System.Linq;
using FluentAssertions;
using Xunit;

namespace LiteDB.Tests.Issues
{
    public class Issue2928_Tests
    {
        private static readonly BsonDocument _doc = new BsonDocument { ["a"] = new BsonArray { 1, 2, 3 } };

        [Theory]
        [InlineData("$.a[0]", 1)]
        [InlineData("$.a[2]", 3)]
        [InlineData("$.a[-1]", 3)]
        [InlineData("$.a[-3]", 1)]
        public void Indexes_inside_the_array_return_the_element(string expression, int expected)
        {
            BsonExpression.Create(expression).ExecuteScalar(_doc).AsInt32.Should().Be(expected);
        }

        [Theory]
        [InlineData("$.a[3]")]
        [InlineData("$.a[-4]")]
        [InlineData("$.a[-5]")]
        public void Indexes_outside_the_array_return_null_on_both_sides(string expression)
        {
            BsonExpression.Create(expression).ExecuteScalar(_doc).IsNull.Should().BeTrue();
        }

        [Theory]
        [InlineData(3)]
        [InlineData(-4)]
        [InlineData(int.MinValue)]
        public void Parameter_indexes_outside_the_array_return_null(int index)
        {
            BsonExpression.Create("$.a[@0]", new BsonValue(index)).ExecuteScalar(_doc).IsNull.Should().BeTrue();
        }

        [Fact]
        public void A_short_array_in_one_row_does_not_abort_the_query()
        {
            using var db = new LiteDatabase(":memory:");
            var rows = db.GetCollection("rows");
            rows.Insert(new BsonDocument { ["_id"] = 1, ["a"] = new BsonArray { 1, 2, 3 } });
            rows.Insert(new BsonDocument { ["_id"] = 2, ["a"] = new BsonArray { 9 } });

            rows.Find("$.a[-2] = 2").Select(x => x["_id"].AsInt32).Should().Equal(1);
        }
    }
}
