using System.Globalization;
using System.Linq;
using FluentAssertions;
using Xunit;

namespace LiteDB.Tests.Issues
{
    public class Issue2775_Tests
    {
        [Fact]
        public void Scalar_scans_preserve_rows_and_multikey_and_in_still_deduplicate()
        {
            using var db = new LiteDatabase(":memory:");
            var rows = db.GetCollection("rows");
            rows.Insert(Enumerable.Range(1, 100).Select(id => new BsonDocument
            { ["_id"] = id, ["key"] = id % 5, ["tags"] = new BsonArray(1, 2, 3) }));
            rows.EnsureIndex("key");
            rows.EnsureIndex("tags", "tags[*]");
            foreach (var predicate in new[] { "key >= 0", "key BETWEEN 0 AND 4", "key != 100", "tags[*] ANY > 0", "key IN [0,0,1,1,2,3,4]" })
                rows.Find(predicate).Select(d => d["_id"].AsInt32).OrderBy(x => x).Should().Equal(Enumerable.Range(1, 100));
            rows.Count().Should().Be(100);
            db.Execute("SELECT COUNT(*) AS n FROM rows").ToArray().Single()["n"].AsInt32.Should().Be(100);
            db.Execute("SELECT SUM(*.key) AS n FROM rows").ToArray().Single()["n"].AsInt32.Should().Be(200);
            var replay = db.Execute("SELECT COUNT(*) AS n, SUM(*.key) AS s FROM rows").ToArray().Single();
            replay["n"].AsInt32.Should().Be(100);
            replay["s"].AsInt32.Should().Be(200);
        }

        [Fact]
        public void In_seeks_deduplicate_distinct_values_that_collate_equally()
        {
            using var db = new LiteDatabase(new ConnectionString
            { Filename = ":memory:", Collation = new Collation(127, CompareOptions.IgnoreCase) });
            var rows = db.GetCollection("rows");
            rows.Insert(new[]
            {
                new BsonDocument { ["_id"] = 1, ["key"] = "a" },
                new BsonDocument { ["_id"] = 2, ["key"] = "b" }
            });
            rows.EnsureIndex("key");
            var query = rows.Query().Where("key IN ['a','A']");
            query.GetPlan()["index"]["mode"].AsString.Should().Contain(" IN ");
            query.ToArray().Select(d => d["_id"].AsInt32).Should().Equal(1);
        }

        [Theory]
        [InlineData("COUNT(*)", true)]
        [InlineData("{n:COUNT(*)}", true)]
        [InlineData("SUM(*.key)", true)]
        [InlineData("{n:COUNT(*),s:SUM(*.key)}", false)]
        [InlineData("COUNT(UNION(*,*))", false)]
        public void Only_proven_single_pass_aggregates_bypass_replay(string source, bool streaming)
        {
            BsonExpression.Create(source).CanStreamAggregateSource.Should().Be(streaming);
        }
    }
}
