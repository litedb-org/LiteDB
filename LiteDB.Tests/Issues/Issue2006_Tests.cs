using System;
using System.Linq;
using FluentAssertions;
using Xunit;

namespace LiteDB.Tests.Issues
{
    public class Issue2006_Tests
    {
        public class Row { public int Id { get; set; } public DateTime? Date { get; set; } }

        [Fact]
        public void Nullable_date_index_extrema_and_ranges_match_independent_input_after_reopen()
        {
            using var file = new TempFile();
            var epoch = new DateTime(2018, 2, 22, 0, 0, 0, DateTimeKind.Utc);
            var seconds = new[] { 0, 86399, -1, 86400, 1, 86398 };
            var input = seconds.Select((s, i) => new Row { Id = i + 1, Date = epoch.AddSeconds(s) }).ToArray();
            for (var stage = 0; stage < 3; stage++)
            {
                using var db = new LiteDatabase(file.Filename);
                db.UtcDate = true;
                var col = db.GetCollection<Row>("rows");
                if (stage == 0) { col.EnsureIndex(x => x.Date); col.Insert(input.Take(2)); }
                if (stage == 1) col.Insert(input.Skip(2));
                var expected = input.Take(stage == 0 ? 2 : 6).ToArray();
                col.Min(x => x.Date).Should().Be(expected.Min(x => x.Date));
                col.Max(x => x.Date).Should().Be(expected.Max(x => x.Date));
                var cutoff = epoch.AddSeconds(10);
                col.Find(x => x.Date < cutoff).Select(x => x.Id).OrderBy(x => x)
                    .Should().Equal(expected.Where(x => x.Date < cutoff).Select(x => x.Id));
                col.Find(x => x.Date > cutoff).Select(x => x.Id).OrderBy(x => x)
                    .Should().Equal(expected.Where(x => x.Date > cutoff).Select(x => x.Id));
            }
        }
    }
}
