using System;
using System.Linq;
using System.Linq.Expressions;
using FluentAssertions;
using Xunit;

namespace LiteDB.Tests.Issues
{
    public class Issue2779_Tests
    {
        public class Row
        {
            public int Id { get; set; }
            public string Name { get; set; }
        }

        [Theory]
        [InlineData("format")]
        [InlineData("concat")]
        [InlineData("join")]
        [InlineData("distinct")]
        public void Captured_method_calls_match_CLR_and_follow_changed_values(string operation)
        {
            var rows = new[] { new Row { Id = 1, Name = "a.b" }, new Row { Id = 2, Name = "c.b" } };
            using var db = new LiteDatabase(":memory:");
            var col = db.GetCollection<Row>();
            col.Insert(rows);
            foreach (var part in new[] { "a", "c", "missing", "a" })
            {
                var other = "b";
                var names = new[] { part + "." + other, part + "." + other };
                Expression<Func<Row, bool>> predicate;
                switch (operation)
                {
                    case "format": predicate = x => x.Name == string.Format("{0}.{1}", part, other); break;
                    case "concat": predicate = x => x.Name == string.Concat(part, ".", other); break;
                    case "join": predicate = x => x.Name == string.Join(".", new[] { part, other }); break;
                    default: predicate = x => names.Distinct().Contains(x.Name); break;
                }
                var expected = rows.Where(predicate.Compile()).Select(x => x.Id).ToArray();
                col.Find(predicate).Select(x => x.Id).OrderBy(x => x).Should().Equal(expected);
            }
            col.Count().Should().Be(2);
        }
    }
}
