using System;
using System.Linq;
using FluentAssertions;
using Xunit;

namespace LiteDB.Tests.Issues
{
    public class Issue1986_Tests
    {
        public class Row
        {
            public int Id { get; set; }
            public int ParentId { get; set; }
            public int ShowIndex { get; set; }
        }

        [Fact]
        public void Filtered_Max_matches_CLR_including_its_empty_sequence_contract()
        {
            var rows = new[]
            {
                new Row { Id = 1, ParentId = 1, ShowIndex = -9 },
                new Row { Id = 2, ParentId = 2, ShowIndex = 99 },
                new Row { Id = 3, ParentId = 1, ShowIndex = -3 }
            };
            using var db = new LiteDatabase(":memory:");
            var col = db.GetCollection<Row>();
            col.Insert(rows);
            col.Max(x => x.ShowIndex).Should().Be(99);
            col.Find(x => x.ParentId == 1).Max(x => x.ShowIndex).Should().Be(-3);
            col.Find(x => x.ParentId == 1).Select(x => x.Id).OrderBy(x => x).Should().Equal(1, 3);
            // Find returns IEnumerable; the Max in the report is Enumerable.Max.
            Action clr = () => rows.Where(x => x.ParentId == 404).Max(x => x.ShowIndex);
            Action queried = () => col.Find(x => x.ParentId == 404).Max(x => x.ShowIndex);
            clr.Should().Throw<InvalidOperationException>();
            queried.Should().Throw<InvalidOperationException>();
            col.Count().Should().Be(3);
        }
    }
}
