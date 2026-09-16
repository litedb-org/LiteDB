using System;
using FluentAssertions;
using Xunit;

namespace LiteDB.Tests.Issues
{
    public class Issue1851_Tests
    {
        public class Row
        {
            public int Id { get; set; }
            public DateTime Timestamp { get; set; }
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void Date_aggregates_respect_UtcDate_and_preserve_the_instant(bool utc)
        {
            var early = new DateTime(2020, 12, 31, 23, 30, 0, DateTimeKind.Utc);
            var late = early.AddHours(2);
            using var db = new LiteDatabase(":memory:");
            db.UtcDate = utc;
            var col = db.GetCollection<Row>();
            col.Insert(new[] { new Row { Id = 1, Timestamp = late }, new Row { Id = 2, Timestamp = early } });
            foreach (var indexed in new[] { false, true })
            {
                if (indexed) col.EnsureIndex(x => x.Timestamp);
                var max = col.Max(x => x.Timestamp);
                var min = col.Min(x => x.Timestamp);
                max.Kind.Should().Be(utc ? DateTimeKind.Utc : DateTimeKind.Local);
                min.Kind.Should().Be(utc ? DateTimeKind.Utc : DateTimeKind.Local);
                max.ToUniversalTime().Ticks.Should().Be(late.Ticks);
                min.ToUniversalTime().Ticks.Should().Be(early.Ticks);
                col.FindById(1).Timestamp.Kind.Should().Be(max.Kind);
            }
        }
    }
}
