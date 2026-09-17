using System.Linq;
using FluentAssertions;
using Xunit;

namespace LiteDB.Tests.Issues
{
    public class Issue1706RangeStart_Tests
    {
        [Theory]
        [InlineData(1, false)]
        [InlineData(-1, false)]
        [InlineData(1, true)]
        [InlineData(-1, true)]
        public void Reversed_range_terms_seek_from_the_output_start(int order, bool exclusive)
        {
            using var db = new LiteDatabase(":memory:");
            var rows = db.GetCollection("rows");
            rows.Insert(Enumerable.Range(1, 100).Select(id => new BsonDocument { ["_id"] = id, ["key"] = id / 2 }));
            rows.EnsureIndex("key");
            var lower = exclusive ? "10 < key" : "10 <= key";
            var upper = exclusive ? "key < 30" : "key <= 30";
            var first = rows.Query().Where(lower + " AND " + upper).OrderBy("key", order);
            var second = rows.Query().Where(upper + " AND " + lower).OrderBy("key", order);
            var expected = Enumerable.Range(1, 100).Where(id => exclusive ? id / 2 > 10 && id / 2 < 30 : id / 2 >= 10 && id / 2 <= 30).ToArray();
            first.ToArray().Select(d => d["_id"].AsInt32).OrderBy(x => x).Should().Equal(expected);
            second.ToArray().Select(d => d["_id"].AsInt32).OrderBy(x => x).Should().Equal(expected);
            first.GetPlan()["index"]["mode"].Should().Be(second.GetPlan()["index"]["mode"]);
            first.GetPlan()["index"]["mode"].AsString.Should().Contain(order == 1 ? "key >" : "key <");
            var expectedKeys = expected.Select(id => id / 2).OrderBy(key => order * key).Take(5);
            first.Limit(5).ToArray().Select(d => d["key"].AsInt32).Should().Equal(expectedKeys);
            second.Limit(5).ToArray().Select(d => d["key"].AsInt32).Should().Equal(expectedKeys);
        }

        [Theory]
        [InlineData(double.MaxValue)]
        [InlineData(double.PositiveInfinity)]
        [InlineData(double.NaN)]
        public void Unlike_numeric_bounds_cannot_introduce_a_planning_overflow(double bound)
        {
            using var db = new LiteDatabase(":memory:");
            var rows = db.GetCollection("rows");
            rows.Insert(Enumerable.Range(1, 10).Select(id => new BsonDocument { ["_id"] = id, ["key"] = (double)id }));
            rows.EnsureIndex("key");
            foreach (var order in new[] { Query.Ascending, Query.Descending })
            foreach (var operation in new[] { "<=", ">" })
            foreach (var reversed in new[] { false, true })
            {
                var first = "key >= @0";
                var second = "key " + operation + " @1";
                var query = BsonExpression.Create(reversed ? second + " AND " + first : first + " AND " + second,
                    new BsonValue(0), new BsonValue(bound));
                var expected = Enumerable.Range(1, 10).Where(id => operation == "<=" ?
                    ((double)id).CompareTo(bound) <= 0 : ((double)id).CompareTo(bound) > 0);
                rows.Query().Where(query).OrderBy("key", order).ToArray()
                    .Select(d => d["_id"].AsInt32).OrderBy(x => x).Should().Equal(expected);
            }
        }

        [Fact]
        public void Independent_multikey_bounds_keep_their_separate_witnesses()
        {
            using var db = new LiteDatabase(":memory:");
            var rows = db.GetCollection("rows");
            rows.Insert(new[]
            {
                new BsonDocument { ["_id"] = 1, ["keys"] = new BsonArray(1, 100) },
                new BsonDocument { ["_id"] = 2, ["keys"] = new BsonArray(50) }
            });
            rows.EnsureIndex("keys", "keys[*]");
            foreach (var predicate in new[] { "keys[*] ANY > 90 AND keys[*] ANY < 10", "keys[*] ANY < 10 AND keys[*] ANY > 90" })
                rows.Find(predicate).Select(d => d["_id"].AsInt32).Should().Equal(1);
        }
    }
}
