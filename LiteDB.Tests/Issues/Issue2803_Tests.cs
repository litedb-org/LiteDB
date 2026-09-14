using System;
using System.Linq;
using FluentAssertions;
using Xunit;

namespace LiteDB.Tests.Issues
{
    public class Issue2803_Tests
    {
        [Fact]
        public void Failed_unique_index_preserves_original_error_data_and_ability_to_retry()
        {
            using var file = new TempFile();
            using (var db = new LiteDatabase(file.Filename))
            {
                var col = db.GetCollection("rows");
                col.InsertBulk(Enumerable.Range(1, 128).Select(i =>
                    new BsonDocument { ["_id"] = i, ["key"] = i == 128 ? 1 : i, ["payload"] = "row" + i }));
                Action index = () => col.EnsureIndex("key", true);
                index.Should().Throw<LiteException>().Which.ErrorCode.Should().Be(LiteException.INDEX_DUPLICATE_KEY);
                col.FindAll().OrderBy(x => x["_id"].AsInt32).Select(x => x["payload"].AsString)
                    .Should().Equal(Enumerable.Range(1, 128).Select(i => "row" + i));
                col.Update(new BsonDocument { ["_id"] = 128, ["key"] = 128, ["payload"] = "row128" }).Should().BeTrue();
                col.EnsureIndex("key", true).Should().BeTrue();
            }
            using (var db = new LiteDatabase(file.Filename))
            {
                var col = db.GetCollection("rows");
                col.Find(Query.EQ("key", 128)).Single()["payload"].AsString.Should().Be("row128");
                col.Count().Should().Be(128);
                Action duplicate = () => col.Insert(new BsonDocument { ["_id"] = 129, ["key"] = 1 });
                duplicate.Should().Throw<LiteException>().Which.ErrorCode.Should().Be(LiteException.INDEX_DUPLICATE_KEY);
            }
        }
    }
}
