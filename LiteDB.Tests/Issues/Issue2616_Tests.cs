using System;
using FluentAssertions;
using LiteDB;
using Xunit;

namespace LiteDB.Tests.Issues;

public class Issue2616_Tests
{
    [Fact]
    public void Checkpoint_Should_Not_Throw_When_Readers_Are_Active()
    {
        using var temp = new TempFile();

        using (var db = new LiteDatabase(temp.Filename))
        {
            db.CheckpointSize = int.MaxValue;
            var col = db.GetCollection<BsonDocument>("items");

            for (var i = 0; i < 32; i++)
            {
                col.Insert(new BsonDocument { ["_id"] = i });
            }

            db.Timeout = TimeSpan.FromSeconds(1);

            var enumerator = col.FindAll().GetEnumerator();
            enumerator.MoveNext().Should().BeTrue();

            db.Invoking(x => x.Checkpoint()).Should().NotThrow();

            enumerator.Dispose();

            db.Invoking(x => x.Checkpoint()).Should().NotThrow();
        }
    }
}
