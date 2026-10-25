using System.Linq;
using FluentAssertions;
using Xunit;

namespace LiteDB.Tests.Issues
{
    public class Issue2113_Tests
    {
        [Fact]
        public void Fluent_group_star_matches_SQL_and_independently_expected_members()
        {
            using var db = new LiteDatabase(":memory:");
            var col = db.GetCollection("orders");
            col.Insert(new[]
            {
                new BsonDocument { ["_id"] = 1, ["Name"] = "a", ["isAccept"] = false },
                new BsonDocument { ["_id"] = 2, ["Name"] = "a", ["isAccept"] = false },
                new BsonDocument { ["_id"] = 3, ["Name"] = "b", ["isAccept"] = false },
                new BsonDocument { ["_id"] = 4, ["Name"] = "a", ["isAccept"] = true }
            });
            var sql = db.Execute("SELECT * FROM orders WHERE isAccept=false GROUP BY Name HAVING COUNT(*)>1;").ToArray();
            sql.Should().HaveCount(1);
            sql[0]["expr"].AsArray.Select(x => x["_id"].AsInt32).Should().Equal(1, 2);
            var fluent = col.Query().Where("isAccept=false").GroupBy("Name").Having("COUNT(*)>1").Select("*").ToArray();
            fluent.Select(x => JsonSerializer.Serialize(x)).Should().Equal(sql.Select(x => JsonSerializer.Serialize(x)));
            col.Count().Should().Be(4);
        }
    }
}
