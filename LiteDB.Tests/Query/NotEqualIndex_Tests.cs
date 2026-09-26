using System;
using System.IO;
using System.Linq;
using FluentAssertions;
using LiteDB.Engine;
using Xunit;

namespace LiteDB.Tests.QueryTest
{
    public class NotEqualIndex_Tests
    {
        [Theory]
        [InlineData("en-US/IgnoreCase", "a", "A")]
        [InlineData("en-US/IgnoreNonSpace", "e", "é")]
        public void Indexed_not_equal_uses_database_collation(string collation, string excluded, string equivalent)
        {
            using var db = new LiteDatabase(new ConnectionString { Filename = ":memory:", Collation = new Collation(collation) });
            var rows = db.GetCollection("rows");
            rows.InsertBulk(new[] { excluded, equivalent, "z" }.Select((s, i) => new BsonDocument { ["_id"] = i + 1, ["Name"] = s }));
            var predicate = BsonExpression.Create("Name != @0", excluded);
            var expected = rows.Find(predicate).Select(x => x["_id"]).ToArray();
            rows.EnsureIndex("name", "Name");
            rows.Find(predicate).Select(x => x["_id"]).Should().BeEquivalentTo(expected);
            rows.Count(predicate).Should().Be(1);
        }

        [Theory]
        [InlineData(-2)]
        [InlineData(-1)]
        [InlineData(0)]
        [InlineData(1)]
        [InlineData(2)]
        public void Exclusion_preserves_order_pages_and_repeated_keys(int excluded)
        {
            using var db = new LiteDatabase(":memory:");
            var rows = db.GetCollection<Row>("rows");
            rows.InsertBulk(Enumerable.Range(1, 2000).Select(i => new Row { Id = i, Score = i % 100 == 0 ? i % 3 - 1 : 0 }));
            var expected = rows.Find(x => x.Score != excluded).Select(x => x.Score).OrderBy(x => x).ToArray();
            rows.EnsureIndex(x => x.Score);
            rows.Query().Where(x => x.Score != excluded).OrderBy(x => x.Score).ToArray()
                .Select(x => x.Score).Should().Equal(expected);
            rows.Query().Where(x => excluded != x.Score).OrderByDescending(x => x.Score).Offset(3).Limit(5).ToArray()
                .Select(x => x.Score).Should().Equal(expected.AsEnumerable().Reverse().Skip(3).Take(5));
            rows.Count(x => x.Score != excluded).Should().Be(expected.Length);
        }

        [Fact]
        public void Mixed_numeric_null_and_extreme_bounds_match_unindexed_execution()
        {
            using var db = new LiteDatabase(":memory:");
            var rows = db.GetCollection("rows");
            var values = new BsonValue[] { BsonValue.Null, 0, 0L, 0.0, 0m, 1, "x", new BsonArray(1, 2) };
            rows.InsertBulk(values.Select((v, i) => new BsonDocument { ["_id"] = i + 1, ["Value"] = v }));
            var excluded = values.Concat(new[] { BsonValue.MinValue, BsonValue.MaxValue }).ToArray();
            var expected = excluded.Select(v => rows.Find(BsonExpression.Create("Value != @0", v))
                .Select(x => x["_id"]).ToArray()).ToArray();
            rows.EnsureIndex("value", "Value");
            for (var i = 0; i < excluded.Length; i++)
            {
                rows.Find(BsonExpression.Create("Value != @0", excluded[i])).Select(x => x["_id"])
                    .Should().BeEquivalentTo(expected[i]);
            }
        }

        [Fact]
        public void Multikey_exclusion_deduplicates_documents_across_both_sides()
        {
            using var db = new LiteDatabase(":memory:");
            var rows = db.GetCollection("rows");
            rows.Insert(new BsonDocument { ["_id"] = 1, ["Values"] = new BsonArray(-1, 0, 1) });
            rows.Insert(new BsonDocument { ["_id"] = 2, ["Values"] = new BsonArray(0, 0) });
            rows.Insert(new BsonDocument { ["_id"] = 3, ["Values"] = new BsonArray(1, 2) });
            rows.Insert(new BsonDocument { ["_id"] = 4, ["Values"] = new BsonArray() });
            var expected = rows.Find("Values[*] ANY != 0").Select(x => x["_id"]).ToArray();
            rows.EnsureIndex("values", "Values[*]");
            var query = rows.Query().Where("Values[*] ANY != 0");
            query.GetPlan()["index"]["name"].AsString.Should().Be("values");
            query.ToArray().Select(x => x["_id"]).Should().BeEquivalentTo(expected);
            query.Count().Should().Be(2);
        }

        [Fact]
        public void Exclusion_handles_empty_indexes_and_later_updates()
        {
            using var db = new LiteDatabase(":memory:");
            var rows = db.GetCollection<Row>("rows");
            rows.EnsureIndex(x => x.Score);
            rows.Count(x => x.Score != 0).Should().Be(0);
            rows.Insert(new Row { Id = 1, Score = 0 });
            rows.Count(x => x.Score != 0).Should().Be(0);
            rows.Update(new Row { Id = 1, Score = 1 });
            rows.Count(x => x.Score != 0).Should().Be(1);
            rows.Delete(1);
            rows.Count(x => x.Score != 0).Should().Be(0);
        }

        [Theory]
        [InlineData(Query.Ascending)]
        [InlineData(Query.Descending)]
        public void Exclusion_survives_page_release_between_results(int order)
        {
            using var engine = new LiteEngine(new EngineSettings
            {
                DataStream = new MemoryStream(), LogStream = new MemoryStream(),
                CacheSize = 65536, TransactionPageLimit = 2
            });
            using var db = new LiteDatabase(engine, disposeOnClose: false);
            var rows = db.GetCollection<Row>("rows");
            rows.InsertBulk(Enumerable.Range(1, 2000).Select(i => new Row { Id = i, Score = i % 3 - 1 }));
            rows.EnsureIndex(x => x.Score);
            var result = rows.Query().Where(x => x.Score != 0).OrderBy(x => x.Score, order).ToArray();
            var expected = Enumerable.Range(1, 2000).Where(i => i % 3 != 1).ToArray();
            result.Select(x => x.Id).Should().BeEquivalentTo(expected);
            result.Select(x => x.Score * order).Should().BeInAscendingOrder();
            rows.Count(x => x.Score != 0).Should().Be(expected.Length);
            rows.Query().Where(x => x.Id != 1000).OrderBy(x => x.Id, order).ToArray()
                .Select(x => x.Id * order).Should().BeInAscendingOrder();
            rows.Count(x => x.Id != 1000).Should().Be(1999);
        }

        public class Row
        {
            public int Id { get; set; }
            public int Score { get; set; }
        }
    }
}
