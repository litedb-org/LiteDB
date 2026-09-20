using System;
using System.Linq;
using FluentAssertions;
using Xunit;

namespace LiteDB.Tests.QueryTest
{
    public class TopNOptimization_Tests
    {
        [Theory]
        [InlineData(0, 1)]
        [InlineData(0, 10)]
        [InlineData(7, 20)]
        [InlineData(500, 500)]
        [InlineData(1014, 10)]
        [InlineData(1015, 10)]
        [InlineData(5000, 20)]
        [InlineData(0, 0)]
        public void Limited_sort_matches_full_sort_with_mixed_directions(int offset, int limit)
        {
            using var db = CreateDatabase(1200);
            var rows = db.GetCollection("rows");
            var full = rows.Query().OrderBy("Score % 17").ThenByDescending("_id").ToArray()
                .Skip(offset).Take(limit).Select(x => x["_id"].AsInt32);
            rows.Query().OrderBy("Score % 17").ThenByDescending("_id").Offset(offset).Limit(limit).ToArray()
                .Select(x => x["_id"].AsInt32).Should().Equal(full);
        }

        [Theory]
        [InlineData(1)]
        [InlineData(-1)]
        public void Single_key_and_stable_ties_keep_collation_and_null_order(int order)
        {
            var collation = new Collation("en-US/IgnoreCase");
            using var db = new LiteDatabase(new ConnectionString { Filename = ":memory:", Collation = collation });
            var rows = db.GetCollection("rows");
            var values = new BsonValue[] { "b", BsonValue.Null, "A", "a", "B", 1, 1.0, 2L };
            rows.InsertBulk(Enumerable.Range(1, 100).Select(i => new BsonDocument { ["_id"] = i, ["Value"] = values[i % values.Length] }));
            var full = rows.Query().OrderBy("Value", order).ToArray().Skip(3).Take(17).Select(x => x["_id"].AsInt32);
            rows.Query().OrderBy("Value", order).Offset(3).Limit(17).ToArray().Select(x => x["_id"].AsInt32).Should().Equal(full);
        }

        [Fact]
        public void Limited_index_only_sort_and_aggregate_replay_remain_correct()
        {
            using var db = CreateDatabase(100);
            var rows = db.GetCollection("rows");
            var query = rows.Query().OrderBy("_id % 7").ThenByDescending("_id")
                .Select("{ total: SUM(*._id), largest: MAX(*._id) }").Offset(3).Limit(10);
            query.GetPlan()["lookup"]["loader"].AsString.Should().Be("index");
            var expected = Enumerable.Range(1, 100).OrderBy(x => x % 7).ThenByDescending(x => x).Skip(3).Take(10).ToArray();
            var result = query.ToDocuments().Single();
            result["total"].AsInt32.Should().Be(expected.Sum());
            result["largest"].AsInt32.Should().Be(expected.Max());
        }

        [Fact]
        public void Discarded_keys_are_still_evaluated_and_validated()
        {
            using var db = new LiteDatabase(":memory:");
            var rows = db.GetCollection("rows");
            rows.Insert(new BsonDocument { ["Name"] = "a" });
            rows.Insert(new BsonDocument { ["Name"] = new string('z', 2000) });
            Action oversized = () => rows.Query().OrderBy("Name").Limit(1).ToArray();
            oversized.Should().Throw<LiteException>();
            Action throwing = () => rows.Query().OrderBy("SUBSTRING(Name, 1000)").Limit(1).ToArray();
            throwing.Should().Throw<ArgumentOutOfRangeException>();
        }

        [Fact]
        public void Included_sort_values_are_reexpanded_after_top_n_selection()
        {
            using var db = new LiteDatabase(":memory:");
            db.GetCollection("owners").InsertBulk(Enumerable.Range(1, 20).Select(i => new BsonDocument
            {
                ["_id"] = i, ["Weight"] = 21 - i
            }));
            var rows = db.GetCollection("rows");
            rows.InsertBulk(Enumerable.Range(1, 20).Select(i => new BsonDocument
            {
                ["_id"] = i, ["Owner"] = new BsonDocument { ["$id"] = i, ["$ref"] = "owners" }
            }));
            var result = rows.Query().Include("Owner").OrderBy("Owner.Weight").ThenBy("_id")
                .Offset(2).Limit(4).ToArray();
            result.Select(x => x["_id"].AsInt32).Should().Equal(18, 17, 16, 15);
            result.Select(x => x["Owner"]["Weight"].AsInt32).Should().Equal(3, 4, 5, 6);
        }

        [Fact]
        public void Randomized_limits_match_independent_ordering()
        {
            using var db = CreateDatabase(300);
            var rows = db.GetCollection("rows");
            var random = new Random(942);
            for (var i = 0; i < 40; i++)
            {
                var divisor = random.Next(2, 29);
                var offset = random.Next(0, 290);
                var limit = random.Next(1, 50);
                var expected = Enumerable.Range(1, 300).Where(x => x % 2 == 0).OrderBy(x => (301 - x) % divisor)
                    .ThenByDescending(x => x).Skip(offset).Take(limit);
                var query = rows.Query().Where("_id % 2 = 0").OrderBy("Score % " + divisor).ThenByDescending("_id");
                query.Offset(offset).Limit(limit).ToArray().Select(x => x["_id"].AsInt32).Should().Equal(expected);
            }
        }

        private static LiteDatabase CreateDatabase(int count)
        {
            var db = new LiteDatabase(":memory:");
            db.GetCollection("rows").InsertBulk(Enumerable.Range(1, count).Select(i => new BsonDocument
            {
                ["_id"] = i, ["Score"] = count + 1 - i
            }));
            return db;
        }
    }
}
