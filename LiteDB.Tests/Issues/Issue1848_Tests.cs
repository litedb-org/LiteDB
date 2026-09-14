using System;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using Xunit;

namespace LiteDB.Tests.Issues
{
    public class Issue1848_Tests
    {
        public class Row { public int Id { get; set; } public DateTime Date { get; set; } public string Payload { get; set; } }

        [Fact]
        public void Concurrent_client_side_time_ranges_preserve_expected_rows_during_inserts()
        {
            using var db = new LiteDatabase(":memory:");
            var col = db.GetCollection<Row>("rows");
            var start = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            col.InsertBulk(Enumerable.Range(1, 4000).Select(id => new Row { Id = id, Date = start.AddSeconds(id), Payload = new string((char)('A' + id % 26), 1000) }));
            var end = start.AddSeconds(2001);
            var writer = Task.Run(() => col.InsertBulk(Enumerable.Range(4001, 500).Select(id => new Row { Id = id, Date = start.AddSeconds(id), Payload = "new" })));
            Parallel.For(0, 4, _ =>
            {
                var rows = col.FindAll().Where(x => x.Date > start && x.Date < end).OrderBy(x => x.Date).ToArray();
                rows.Select(x => x.Id).Should().Equal(Enumerable.Range(1, 2000));
                foreach (var row in rows) row.Payload.Should().Be(new string((char)('A' + row.Id % 26), 1000));
            });
            writer.GetAwaiter().GetResult().Should().Be(500);
            col.Count().Should().Be(4500);
        }
    }
}
