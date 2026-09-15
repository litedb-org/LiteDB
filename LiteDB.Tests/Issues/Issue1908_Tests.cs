using System.Linq;
using FluentAssertions;
using Xunit;

namespace LiteDB.Tests.Issues
{
    public class Issue1908_Tests
    {
        [Fact]
        public void Captured_array_index_tracks_mutation_and_selects_exact_documents()
        {
            using var db = new LiteDatabase(":memory:");
            var col = db.GetCollection("factors");
            col.Insert(new BsonDocument { ["_id"] = 1, ["Factor"] = "Base" });
            col.Insert(new BsonDocument { ["_id"] = 2, ["Factor"] = "Other" });
            var parameters = new[] { "Base", "Other", "Missing" };
            foreach (var index in new[] { 0, 1, 2, 0 })
            {
                var expected = col.FindAll().Where(x => x["Factor"].AsString == parameters[index])
                    .Select(x => x["_id"].AsInt32).OrderBy(x => x).ToArray();
                col.Find(x => x["Factor"] == parameters[index]).Select(x => x["_id"].AsInt32)
                    .OrderBy(x => x).Should().Equal(expected);
            }
            parameters[0] = "Other";
            col.Find(x => x["Factor"] == parameters[0]).Select(x => x["_id"].AsInt32).Should().Equal(2);
            col.Count().Should().Be(2);
        }
    }
}
