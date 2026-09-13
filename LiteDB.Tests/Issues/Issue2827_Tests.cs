using System;
using System.IO;
using System.Linq;
using FluentAssertions;
using Xunit;

namespace LiteDB.Tests.Issues
{
    public class Issue2827_Tests
    {
        [Theory]
        [InlineData(2400, true)]
        [InlineData(3000, false)]
        [InlineData(3000, true)]
        public void Rebuild_preserves_every_document_and_secondary_index_across_loop_guard_boundary(int count, bool indexed)
        {
            var directory = Path.Combine(Path.GetTempPath(), "litedb-2827-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            var file = Path.Combine(directory, "data.db");
            try
            {
                using (var db = new LiteDatabase(file))
                {
                    var col = db.GetCollection("rows");
                    col.InsertBulk(Enumerable.Range(1, count).Select(i =>
                        new BsonDocument { ["_id"] = i, ["name"] = "user" + i, ["value"] = i * 17 }));
                    if (indexed) col.EnsureIndex("name", true);
                    db.Rebuild();
                    col.Count().Should().Be(count);
                }
                using (var db = new LiteDatabase(file))
                {
                    var col = db.GetCollection("rows");
                    var rows = col.FindAll().OrderBy(x => x["_id"].AsInt32).ToArray();
                    rows.Select(x => x["_id"].AsInt32).Should().Equal(Enumerable.Range(1, count));
                    rows.Select(x => x["value"].AsInt32).Should().Equal(Enumerable.Range(1, count).Select(i => i * 17));
                    col.Find(Query.EQ("name", "user" + count)).Single()["_id"].AsInt32.Should().Be(count);
                    if (indexed)
                    {
                        col.EnsureIndex("name", true).Should().BeFalse("rebuild must retain the original index");
                        Action duplicate = () => col.Insert(new BsonDocument { ["_id"] = count + 1, ["name"] = "user1" });
                        duplicate.Should().Throw<LiteException>().Which.ErrorCode.Should().Be(LiteException.INDEX_DUPLICATE_KEY);
                    }
                }
            }
            finally { Directory.Delete(directory, true); }
        }
    }
}
