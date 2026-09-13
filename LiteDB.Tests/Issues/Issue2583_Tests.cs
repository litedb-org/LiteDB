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
            var expected = direction == "asc" ? values.OrderBy(x => x).ToArray() : values.OrderByDescending(x => x).ToArray();
            using var reader = db.Execute("SELECT amount, amount + 20 AS plus20 FROM invoices ORDER BY plus20 " + direction);
            var rows = reader.ToArray();
            rows.Select(x => x["amount"].AsInt32).Should().Equal(expected);
            rows.Select(x => x["plus20"].AsInt32).Should().Equal(expected.Select(x => x + 20));
            db.GetCollection("invoices").Count().Should().Be(4);
        }
    }
}
