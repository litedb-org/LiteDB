using System;
using System.IO;
using System.Linq;
using FluentAssertions;
using Xunit;

namespace LiteDB.Tests.Issues;

public class Issue2587_Tests
{
    private class Item
    {
        public int Id { get; set; }
        public int Value { get; set; }
    }

    [Fact]
    public void DeleteMany_Should_Not_Grow_Log_Unbounded()
    {
        using var temp = new TempFile();

        using (var db = new LiteDatabase(temp.Filename))
        {
            var col = db.GetCollection<Item>("items");
            col.InsertBulk(Enumerable.Range(0, 2000).Select(i => new Item { Id = i + 1, Value = i }));
        }

        using (var db = new LiteDatabase(temp.Filename))
        {
            db.CheckpointSize = int.MaxValue;
            var col = db.GetCollection<Item>("items");
            var values = Enumerable.Range(0, 2000).ToArray();

            col.DeleteMany(x => values.Contains(x.Value));

            var logFile = FileHelper.GetLogFile(temp.Filename);
            var logSize = new FileInfo(logFile).Exists ? new FileInfo(logFile).Length : 0;

            logSize.Should().BeLessThan(5 * 1024 * 1024);
        }
    }
}
