using System.Linq;
using FluentAssertions;
using Xunit;

namespace LiteDB.Tests.QueryTest
{
    public class IndexLookupReplay_Tests
    {
        [Fact]
        public void Multiple_aggregates_can_replay_index_only_values()
        {
            using var db = CreateDatabase();
            var query = db.GetCollection("rows").Query().Select("{ total: SUM(*._id), largest: MAX(*._id), n: COUNT(*._id) }");
            query.GetPlan()["lookup"]["loader"].AsString.Should().Be("index");
            var result = query.ToDocuments().Single();
            result["total"].AsInt32.Should().Be(55);
            result["largest"].AsInt32.Should().Be(10);
            result["n"].AsInt32.Should().Be(10);
        }

        [Fact]
        public void Grouping_by_a_constant_reloads_the_index_after_sorting()
        {
            using var db = CreateDatabase();
            var rows = db.GetCollection<RangeOptimization_Tests.Row>("rows");
            rows.Query().GroupBy(x => 0).Count().Should().Be(10);
            rows.Query().GroupBy(x => 0).LongCount().Should().Be(10);
            rows.Query().GroupBy(x => 0).Exists().Should().BeTrue();
        }

        [Fact]
        public void Computed_sort_and_pagination_reload_the_selected_index_key()
        {
            using var db = CreateDatabase();
            var query = db.GetCollection("rows").Query().OrderBy("_id % 3").ThenByDescending("_id")
                .Select("{ id: _id }").Offset(1).Limit(4);
            query.GetPlan()["lookup"]["loader"].AsString.Should().Be("index");
            query.ToDocuments().Select(x => x["id"].AsInt32).Should().Equal(
                Enumerable.Range(1, 10).OrderBy(x => x % 3).ThenByDescending(x => x).Skip(1).Take(4));
        }

        [Fact]
        public void Secondary_index_replay_keeps_null_and_collated_keys()
        {
            using var db = new LiteDatabase(new ConnectionString { Filename = ":memory:", Collation = new Collation("en-US/IgnoreCase") });
            var rows = db.GetCollection("rows");
            var keys = new BsonValue[] { "a", "A", "b", BsonValue.Null };
            rows.InsertBulk(keys.Select(x => new BsonDocument { ["Name"] = x }));
            rows.EnsureIndex("name", "Name");
            var query = rows.Query().Select("{ n: COUNT(*.Name), keys: ARRAY(*.Name) }");
            query.GetPlan()["lookup"]["loader"].AsString.Should().Be("index");
            var result = query.ToDocuments().Single();
            result["n"].AsInt32.Should().Be(4);
            result["keys"].AsArray.ToArray().Should().BeEquivalentTo(keys);
        }

        private static LiteDatabase CreateDatabase()
        {
            var db = new LiteDatabase(":memory:");
            db.GetCollection("rows").InsertBulk(Enumerable.Range(1, 10).Select(i => new BsonDocument { ["_id"] = i }));
            return db;
        }
    }
}
