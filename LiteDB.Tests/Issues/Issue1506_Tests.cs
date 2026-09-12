using System.Linq;
using FluentAssertions;
using Xunit;

namespace LiteDB.Tests.Issues
{
    public class Issue1506_Tests
    {
        [Fact]
        public void Find_honors_query_paging_and_does_not_mutate_reusable_query()
        {
            using var db = new LiteDatabase(":memory:");
            var col = db.GetCollection("rows");
            col.Insert(Enumerable.Range(1, 6).Select(i => new BsonDocument { ["_id"] = i }));
            var query = Query.All("_id");
            query.Offset = 1;
            query.Limit = 2;
            col.Find(query).Select(x => x["_id"].AsInt32).Should().Equal(2, 3);
            col.Find(query, skip: 3, limit: 1).Select(x => x["_id"].AsInt32).Should().Equal(4);
            query.Offset.Should().Be(1, "a later use must retain the caller's query settings");
            query.Limit.Should().Be(2);
            col.Find(query).Select(x => x["_id"].AsInt32).Should().Equal(2, 3);
            col.Count().Should().Be(6);
        }
    }
}
