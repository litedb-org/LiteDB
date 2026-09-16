using System.Globalization;
using System.Linq;
using FluentAssertions;
using Xunit;

namespace LiteDB.Tests.Issues
{
    public class Issue2205_Tests
    {
        [Fact]
        public void Huge_numeric_looking_strings_are_preserved_when_quoted_or_parameterized()
        {
            const string digits = "111111111111111111111111111111111111";
            using var db = new LiteDatabase(":memory:");
            var col = db.GetCollection("rows");
            col.Insert(new BsonDocument { ["_id"] = 1, ["Name"] = digits });
            col.Insert(new BsonDocument { ["_id"] = 2, ["Name"] = digits + "2" });
            col.Find("$.Name = '" + digits + "'").Select(x => x["_id"].AsInt32).Should().Equal(1);
            col.Find(BsonExpression.Create("$.Name = @0", new BsonValue(digits))).Select(x => x["_id"].AsInt32).Should().Equal(1);
            col.FindById(1)["Name"].AsString.Should().Be(digits);
        }

        [Fact]
        public void Out_of_range_unquoted_numeric_token_is_a_number_like_every_shorter_token()
        {
            const string digits = "111111111111111111111111111111111111";
            using var db = new LiteDatabase(":memory:");
            var col = db.GetCollection("rows");
            col.Insert(new[]
            {
                new BsonDocument { ["_id"] = 1, ["Name"] = digits },
                new BsonDocument { ["_id"] = 2, ["Name"] = "123" },
                new BsonDocument { ["_id"] = 3, ["Name"] = double.Parse(digits, CultureInfo.InvariantCulture) }
            });

            // An unquoted token never matches a string field, whatever its length; the query no longer throws.
            col.Find("$.Name = 123").Should().BeEmpty();
            col.Find("$.Name = " + digits).Select(x => x["_id"].AsInt32).Should().Equal(3);
            col.Find("$.Name > " + digits).Select(x => x["_id"].AsInt32).Should().Equal(1, 2);
        }
    }
}
