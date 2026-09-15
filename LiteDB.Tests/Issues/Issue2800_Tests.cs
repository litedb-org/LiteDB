using System;
using System.Linq;
using FluentAssertions;
using Xunit;

namespace LiteDB.Tests.Issues
{
    public class Issue2800_Tests
    {
        [Theory]
        [InlineData("COUNT(FILTER($.Items[*] => @ = @0)) > 0")]
        [InlineData("COUNT($.Items[@ = @0]) > 0")]
        [InlineData("MAP($.Items[*] => @ = @0) ANY = true")]
        public void Nested_expression_parameters_are_fresh_across_queries_and_databases(string source)
        {
            using var file = new TempFile();
            using (var db = new LiteDatabase(file.Filename))
            {
                var col = db.GetCollection("items");
                col.Insert(new BsonDocument { ["_id"] = 1, ["Items"] = new BsonArray(11, 13) });
                col.Insert(new BsonDocument { ["_id"] = 2, ["Items"] = new BsonArray(22) });
                col.Insert(new BsonDocument { ["_id"] = 3, ["Items"] = new BsonArray() });
            }
            foreach (var value in new[] { 11, 22, 99, 22, 11 })
            {
                using var db = new LiteDatabase(file.Filename);
                var col = db.GetCollection("items");
                var expected = col.FindAll().Where(x => x["Items"].AsArray.Any(v => v.AsInt32 == value))
                    .Select(x => x["_id"].AsInt32).OrderBy(x => x).ToArray();
                var expression = BsonExpression.Create(source, new BsonValue(value));
                col.Find(expression).Select(x => x["_id"].AsInt32).OrderBy(x => x).Should().Equal(expected);
                expression.Parameters["0"].AsInt32.Should().Be(value);
                col.Count().Should().Be(3);
            }
        }
    }
}
