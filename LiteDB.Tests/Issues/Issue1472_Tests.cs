using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using Xunit;

namespace LiteDB.Tests.Issues
{
    public class Issue1472_Tests
    {
        [Fact]
        public void Parallel_cached_index_queries_keep_their_own_parameters_during_bulk_inserts()
        {
            using var db = new LiteDatabase(":memory:");
            var col = db.GetCollection("rows");
            col.Insert(Enumerable.Range(1, 100).Select(id => new BsonDocument { ["_id"] = id, ["value"] = id % 4, ["payload"] = "row" + id }));
            col.EnsureIndex("value");
            var writer = Task.Run(() => col.Insert(Enumerable.Range(101, 1000).Select(id => new BsonDocument { ["_id"] = id, ["value"] = -1 })));
            Parallel.For(0, 4, worker =>
            {
                for (var round = 0; round < 100; round++)
                {
                    var parameters = new BsonDocument { ["value"] = worker };
                    var query = BsonExpression.Create("value=@value", parameters);
                    var rows = col.Find(query).OrderBy(x => x["_id"].AsInt32).ToArray();
                    rows.Select(x => x["_id"].AsInt32).Should().Equal(Enumerable.Range(1, 100).Where(id => id % 4 == worker));
                    foreach (var row in rows) row["payload"].AsString.Should().Be("row" + row["_id"].AsInt32);
                    parameters.Keys.Should().Equal("value");
                    parameters["value"].AsInt32.Should().Be(worker);
                }
            });
            writer.GetAwaiter().GetResult().Should().Be(1000);
            col.Count().Should().Be(1100);
        }
    }
}
