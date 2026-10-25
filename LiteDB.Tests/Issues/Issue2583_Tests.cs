using System.Linq;
using FluentAssertions;
using Xunit;

namespace LiteDB.Tests.Issues
{
    public class Issue2583_Tests
    {
        [Theory]
        [InlineData("asc")]
        [InlineData("desc")]
        public void Ordering_by_computed_alias_sorts_values_without_losing_rows(string direction)
        {
            using var db = new LiteDatabase(":memory:");
            var values = new[] { 30, -5, 12, 0 };
            db.GetCollection("invoices").Insert(values.Select((value, i) =>
                new BsonDocument { ["_id"] = i + 1, ["amount"] = value }));
            var expected = direction == "asc"
                ? values.OrderByDescending(x => x).ToArray()
                : values.OrderBy(x => x).ToArray();
            using var reader = db.Execute(
                "SELECT amount, 100 - amount AS inverse FROM invoices ORDER BY inverse " + direction);
            var rows = reader.ToArray();
            rows.Select(x => x["amount"].AsInt32).Should().Equal(expected);
            rows.Select(x => x["inverse"].AsInt32).Should().Equal(expected.Select(x => 100 - x));
            db.GetCollection("invoices").Count().Should().Be(4);
        }
    }
}
