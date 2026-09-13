using System.Linq;
using FluentAssertions;
using Xunit;

namespace LiteDB.Tests.Issues
{
    public class Issue1545_Tests
    {
        public class Row
        {
            public int Id { get; set; }
            public int Group { get; set; }
        }

        [Fact]
        public void Captured_grouping_key_matches_each_CLR_group_in_both_orders()
        {
            var rows = Enumerable.Range(1, 7).Select(i => new Row { Id = i, Group = i % 3 }).ToArray();
            using var db = new LiteDatabase(":memory:");
            var col = db.GetCollection<Row>();
            col.Insert(rows);
            foreach (var group in rows.GroupBy(x => x.Group).Concat(rows.Reverse().GroupBy(x => x.Group)))
            {
                col.Find(x => x.Group == group.Key).Select(x => x.Id).OrderBy(x => x)
                    .Should().Equal(group.Select(x => x.Id).OrderBy(x => x));
            }
            col.Count().Should().Be(rows.Length);
        }
    }
}
