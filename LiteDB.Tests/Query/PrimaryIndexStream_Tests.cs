using System.Linq;
using FluentAssertions;
using Xunit;

namespace LiteDB.Tests.QueryTest
{
    public class PrimaryIndexStream_Tests
    {
        [Theory]
        [InlineData("_id IN [1,1,2,2,5]")]
        [InlineData("_id = 1 OR _id = 1 OR _id = 2 OR _id = 5")]
        [InlineData("_id >= 1 AND _id <= 5 AND _id != 3 AND _id != 4")]
        public void Primary_index_seeks_and_ranges_emit_each_document_once(string predicate)
        {
            using var db = new LiteDatabase(":memory:");
            var rows = db.GetCollection("rows");
            rows.InsertBulk(Enumerable.Range(1, 10).Select(i => new BsonDocument { ["_id"] = i }));
            var query = rows.Query().Where(predicate).OrderByDescending("_id");
            query.GetPlan()["index"]["name"].AsString.Should().Be("_id");
            query.ToArray().Select(x => x["_id"].AsInt32).Should().Equal(5, 2, 1);
            query.Count().Should().Be(3);
            query.Exists().Should().BeTrue();
        }

        [Fact]
        public void Collated_primary_in_values_are_deduplicated_before_seeking()
        {
            using var db = new LiteDatabase(new ConnectionString { Filename = ":memory:", Collation = new Collation("en-US/IgnoreCase") });
            var rows = db.GetCollection("rows");
            rows.Insert(new BsonDocument { ["_id"] = "a" });
            rows.Insert(new BsonDocument { ["_id"] = "b" });
            rows.Query().Where("_id IN ['A','a','B','b']").Count().Should().Be(2);
            rows.Query().Where("_id LIKE 'a%'").Count().Should().Be(1);
        }

        [Fact]
        public void Secondary_multikey_indexes_keep_document_deduplication()
        {
            using var db = new LiteDatabase(":memory:");
            var rows = db.GetCollection("rows");
            rows.Insert(new BsonDocument { ["Values"] = new BsonArray(1, 3, 5) });
            rows.Insert(new BsonDocument { ["Values"] = new BsonArray(2, 4, 6) });
            rows.EnsureIndex("values", "Values[*]");
            var query = rows.Query().Where("Values[*] ANY >= 1");
            query.GetPlan()["index"]["name"].AsString.Should().Be("values");
            query.ToArray().Length.Should().Be(2);
            query.Count().Should().Be(2);
            rows.Query().Where("Values[*] ANY IN [1,2,3,4,5,6]").Count().Should().Be(2);
        }
    }
}
