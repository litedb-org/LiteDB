using System;
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
        public void Out_of_range_numeric_literal_produces_a_query_error_instead_of_CLR_overflow()
        {
            Action parse = () => BsonExpression.Create("$.Name = 111111111111111111111111111111111111");
            parse.Should().Throw<LiteException>();
        }
    }
}
