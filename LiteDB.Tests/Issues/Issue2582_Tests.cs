using System.Linq;
using FluentAssertions;
using LiteDB;
using Xunit;

namespace LiteDB.Tests.Issues;

public class Issue2582_Tests
{
    [Fact]
    public void Rebuild_Should_Handle_Large_Collections()
    {
        using var temp = new TempFile();

        using (var db = new LiteDatabase(temp.Filename))
        {
            db.CheckpointSize = int.MaxValue;
            var col = db.GetCollection<BsonDocument>("items");

            foreach (var id in Enumerable.Range(0, 6000))
            {
                col.Insert(new BsonDocument
                {
                    ["_id"] = id,
                    ["name"] = $"item-{id}"
                });
            }
        }

        using (var db = new LiteDatabase(temp.Filename))
        {
            db.Invoking(x => x.Rebuild()).Should().NotThrow();
        }
    }
}
