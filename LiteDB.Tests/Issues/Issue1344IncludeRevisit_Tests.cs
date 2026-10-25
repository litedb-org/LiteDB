using System.IO;
using Xunit;

namespace LiteDB.Tests.Issues
{
    public class Issue1344IncludeRevisit_Tests
    {
        [Fact]
        public void Include_reacquisition_preserves_existing_read_your_writes_behavior()
        {
            using var db = new LiteDatabase(":memory:");
            using (var source = new MemoryStream(new byte[] { 1 }))
                db.FileStorage.Upload("file", "file", source, new BsonDocument { ["value"] = "old" });
            db.GetCollection("other").Insert(new BsonDocument { ["_id"] = "other", ["value"] = "other" });
            var rows = db.GetCollection("rows");
            rows.Insert(new[]
            {
                new BsonDocument { ["_id"] = 1, ["item"] = Reference("_files", "file") },
                new BsonDocument { ["_id"] = 2, ["item"] = Reference("other", "other") },
                new BsonDocument { ["_id"] = 3, ["item"] = Reference("_files", "file") }
            });
            Assert.True(db.BeginTrans());
            using (var cursor = rows.Query().Include("$.item").ToEnumerable().GetEnumerator())
            {
                Assert.True(cursor.MoveNext());
                Assert.Equal("old", cursor.Current["item"]["metadata"]["value"].AsString);
                Assert.True(db.FileStorage.SetMetadata("file", new BsonDocument { ["value"] = "new" }));
                Assert.True(cursor.MoveNext());
                Assert.Equal("other", cursor.Current["item"]["value"].AsString);
                Assert.True(cursor.MoveNext());
                Assert.Equal("new", cursor.Current["item"]["metadata"]["value"].AsString);
                Assert.False(cursor.MoveNext());
            }
            Assert.True(db.Rollback());
        }

        private static BsonDocument Reference(string collection, string id)
            => new BsonDocument { ["$ref"] = collection, ["$id"] = id };
    }
}
