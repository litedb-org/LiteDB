using System.IO;
using FluentAssertions;
using Xunit;

namespace LiteDB.Tests.Issues;

public class Issue2618_Tests
{
    [Fact]
    public void Upsert_Should_Flush_On_Dispose()
    {
        using var temp = new TempFile();

        using (var db = new LiteDatabase(temp.Filename))
        {
            var col = db.GetCollection<BsonDocument>("items");
            col.Upsert(new BsonDocument {{"_id", 1}, {"name", "old"}});
        }

        using (var db = new LiteDatabase(temp.Filename))
        {
            db.CheckpointSize = 0;
            var col = db.GetCollection<BsonDocument>("items");
            db.BeginTrans();
            col.Upsert(new BsonDocument {{"_id", 1}, {"name", "new"}});
            db.Commit();
        }

        using (var db = new LiteDatabase(temp.Filename))
        {
            var col = db.GetCollection<BsonDocument>("items");
            col.FindById(1)["name"].AsString.Should().Be("new");
        }
    }
}
