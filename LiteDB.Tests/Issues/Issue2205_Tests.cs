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
        public void Out_of_range_unquoted_numeric_token_matches_the_complete_string_value()
        {
            const string digits = "111111111111111111111111111111111111";
            using var db = new LiteDatabase(":memory:");
            var col = db.GetCollection("rows");
            col.Insert(new[]
            {
                new BsonDocument { ["_id"] = 1, ["Name"] = digits, ["marker"] = "exact" },
                new BsonDocument { ["_id"] = 2, ["Name"] = digits + "2", ["marker"] = "suffix" },
                new BsonDocument { ["_id"] = 3, ["Name"] = "1" + digits, ["marker"] = "prefix" }
            });

            var expression = BsonExpression.Create("$.Name = " + digits);
            var matches = col.Find(expression).ToArray();

            matches.Should().ContainSingle();
            matches[0]["_id"].AsInt32.Should().Be(1);
            matches[0]["Name"].AsString.Should().Be(digits);
            matches[0]["marker"].AsString.Should().Be("exact");
            col.FindById(2)["Name"].AsString.Should().Be(digits + "2");
            col.FindById(3)["Name"].AsString.Should().Be("1" + digits);
        }
    }
}
