using System;
using System.Linq;
using FluentAssertions;
using Xunit;

namespace LiteDB.Tests.Issues
{
    public class Issue2357_Tests
    {
        public class Row { public DateTime Id { get; set; } public int Hour { get; set; } }

        [Fact]
        public void Distinct_UTC_hour_ids_crossing_reported_DST_transition_remain_distinct()
        {
            using var file = new TempFile();
            var start = new DateTime(2006, 3, 23, 0, 0, 0, DateTimeKind.Utc);
            var rows = Enumerable.Range(0, 480).Select(hour => new Row { Id = start.AddHours(hour), Hour = hour }).ToArray();
            using (var db = new LiteDatabase(file.Filename))
            {
                db.GetCollection<Row>("hours").InsertBulk(rows).Should().Be(480);
            }
            using var reopened = new LiteDatabase(file.Filename);
            reopened.UtcDate = true;
            var col = reopened.GetCollection<Row>("hours");
            col.Count().Should().Be(480);
            foreach (var expected in rows)
            {
                var actual = col.FindById(expected.Id);
                actual.Id.Should().Be(expected.Id);
                actual.Hour.Should().Be(expected.Hour);
            }
            col.FindAll().Select(x => x.Id).OrderBy(x => x).Should().Equal(rows.Select(x => x.Id));
        }
    }
}
