using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using Xunit;

namespace LiteDB.Tests.Issues
{
    public class Issue2169_Tests
    {
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void Parallel_insert_or_upsert_preserves_every_acknowledged_row(bool upsert)
        {
            using var file = new TempFile();
            using (var db = new LiteDatabase(file.Filename))
            {
                var col = db.GetCollection("rows");
                col.EnsureIndex("worker"); // Isolate initial collection publication (#2792).
                Parallel.For(0, 8, worker =>
                {
                    for (var sequence = 0; sequence < 100; sequence++)
                    {
                        var id = worker * 100 + sequence + 1;
                        var doc = new BsonDocument { ["_id"] = id, ["worker"] = worker, ["payload"] = new string((char)('A' + worker), 500) };
                        if (upsert) col.Upsert(doc).Should().BeTrue();
                        else col.Insert(doc).AsInt32.Should().Be(id);
                    }
                });
            }
            using var reopened = new LiteDatabase(file.Filename);
            var rows = reopened.GetCollection("rows").FindAll().OrderBy(x => x["_id"].AsInt32).ToArray();
            rows.Select(x => x["_id"].AsInt32).Should().Equal(Enumerable.Range(1, 800));
            foreach (var row in rows)
            {
                var worker = (row["_id"].AsInt32 - 1) / 100;
                row["worker"].AsInt32.Should().Be(worker);
                row["payload"].AsString.Should().Be(new string((char)('A' + worker), 500));
            }
        }
    }
}
