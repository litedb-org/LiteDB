using System;
using System.Linq;
using System.Linq.Expressions;
using FluentAssertions;
using Xunit;

namespace LiteDB.Tests.Issues
{
    public class Issue2847_Tests
    {
        public class Row
        {
            public int Id { get; set; }
            public string Name { get; set; }
        }

        [Theory]
        [InlineData(StringComparison.Ordinal, false)]
        [InlineData(StringComparison.OrdinalIgnoreCase, false)]
        [InlineData(StringComparison.Ordinal, true)]
        [InlineData(StringComparison.OrdinalIgnoreCase, true)]
        public void Explicit_string_comparison_agrees_with_CLR_even_with_an_index(StringComparison comparison, bool indexed)
        {
            var rows = new[]
            {
                new Row { Id = 1, Name = "Alice" }, new Row { Id = 2, Name = "alice" },
                new Row { Id = 3, Name = "ALICE" }, new Row { Id = 4, Name = "Bob" }
            };
            using var db = new LiteDatabase(new ConnectionString { Filename = ":memory:", Collation = new Collation("en-US/IgnoreCase") });
            var col = db.GetCollection<Row>();
            col.Insert(rows);
            if (indexed) col.EnsureIndex(x => x.Name);
            Expression<Func<Row, bool>> predicate = x => x.Name.Equals("alice", comparison);
            // #2847 permits a deliberate NotSupportedException for unsupported modes.
            // Other exceptions, partial results and silently ignoring the mode are failures.
            try
            {
                col.Find(predicate).Select(x => x.Id).OrderBy(x => x).Should()
                    .Equal(rows.Where(predicate.Compile()).Select(x => x.Id));
            }
            catch (NotSupportedException)
            {
                col.Find(x => x.Name == "alice").Select(x => x.Id).OrderBy(x => x).Should().Equal(1, 2, 3);
            }
            col.FindById(4).Name.Should().Be("Bob");
            col.Count().Should().Be(4);
        }
    }
}
