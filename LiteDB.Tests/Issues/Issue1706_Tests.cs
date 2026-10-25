using System.Linq;
using FluentAssertions;
using Xunit;

namespace LiteDB.Tests.Issues
{
    public class Issue1706_Tests
    {
        public class Row { public int Id { get; set; } }

        [Fact]
        public void Flipping_range_conjuncts_keeps_same_scan_strategy_and_exact_top_results()
        {
            using var db = new LiteDatabase(":memory:");
            var col = db.GetCollection<Row>("rows");
            col.InsertBulk(Enumerable.Range(1, 50000).Select(id => new Row { Id = id }));
            var low = 24900;
            var high = 25100;
            var first = col.Query().Where(x => x.Id >= low && x.Id <= high).OrderByDescending(x => x.Id).Limit(10);
            var second = col.Query().Where(x => x.Id <= high && x.Id >= low).OrderByDescending(x => x.Id).Limit(10);
            var expected = Enumerable.Range(high - 9, 10).Reverse();
            first.ToArray().Select(x => x.Id).Should().Equal(expected);
            second.ToArray().Select(x => x.Id).Should().Equal(expected);
            // Compare the selected scan, not noisy elapsed milliseconds or parameter names.
            first.GetPlan()["index"]["mode"].Should().Be(second.GetPlan()["index"]["mode"]);
            col.Count().Should().Be(50000);
        }
    }
}
