using FluentAssertions;
using Xunit;

namespace LiteDB.Tests.Expressions
{
    /// <summary>
    /// Compiled expressions are cached by their Source text. Double constants used to be
    /// formatted with at most nine decimals, so distinct constants shared a Source and the
    /// second expression silently reused the first one's compiled constant.
    /// </summary>
    public class DoubleConstantSource_Tests
    {
        [Theory]
        [InlineData("0.1234567891", "0.1234567894")]
        [InlineData("10.0000000001", "10.0000000004")]
        [InlineData("1E-10", "2E-10")]
        public void Distinct_double_constants_do_not_share_compiled_expressions(string first, string second)
        {
            var firstValue = new BsonValue(double.Parse(first, System.Globalization.CultureInfo.InvariantCulture));
            var secondValue = new BsonValue(double.Parse(second, System.Globalization.CultureInfo.InvariantCulture));
            var field = "dcs" + System.Math.Abs(first.GetHashCode() ^ second.GetHashCode());

            var firstExpression = BsonExpression.Create($"$.{field} = {first}");
            var secondExpression = BsonExpression.Create($"$.{field} = {second}");

            firstExpression.Source.Should().NotBe(secondExpression.Source);
            firstExpression.ExecuteScalar(new BsonDocument { [field] = firstValue }).AsBoolean.Should().BeTrue();
            secondExpression.ExecuteScalar(new BsonDocument { [field] = secondValue }).AsBoolean
                .Should().BeTrue("the second constant must not reuse the first constant's compiled expression");
            BsonExpression.Create(secondExpression.Source).ExecuteScalar(new BsonDocument { [field] = secondValue })
                .AsBoolean.Should().BeTrue("Source must parse back to the same constant");

            using var db = new LiteDatabase(":memory:");
            var rows = db.GetCollection("rows");
            rows.Insert(new BsonDocument { ["_id"] = 1, [field] = firstValue });
            rows.Insert(new BsonDocument { ["_id"] = 2, [field] = secondValue });
            rows.Count(Query.EQ(field, firstValue)).Should().Be(1);
            rows.Count(Query.EQ(field, secondValue)).Should().Be(1);
            rows.EnsureIndex(field);
            rows.FindOne(Query.EQ(field, firstValue))["_id"].AsInt32.Should().Be(1);
            rows.FindOne(Query.EQ(field, secondValue))["_id"].AsInt32.Should().Be(2);
        }

        [Fact]
        public void Short_double_sources_keep_their_fixed_form()
        {
            BsonExpression.Create("1.5").Source.Should().Be("1.5");
            BsonExpression.Create("2.0").Source.Should().Be("2.0");
            BsonExpression.Create("0.1").Source.Should().Be("0.1");
        }
    }
}
