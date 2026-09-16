using System;
using System.Linq;
using System.Linq.Expressions;
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

        public class KeyPart
        {
            public int Number { get; set; }
        }

        [Fact]
        public void Reused_predicate_reads_the_current_captured_group_and_composite_key_member()
        {
            var rows = Enumerable.Range(1, 6).Select(i => new Row { Id = i, Group = i % 3 }).ToArray();
            var groups = rows.GroupBy(x => x.Group).ToArray();
            IGrouping<int, Row> current = groups[0];
            Expression<Func<Row, bool>> predicate = x => x.Group == current.Key;
            using var db = new LiteDatabase(":memory:");
            var col = db.GetCollection<Row>();
            col.Insert(rows);
            foreach (var group in groups.Concat(groups.AsEnumerable().Reverse()))
            {
                current = group;
                col.Find(predicate).Select(x => x.Id).OrderBy(x => x)
                    .Should().Equal(group.Select(x => x.Id).OrderBy(x => x));
            }
            foreach (var group in rows.GroupBy(x => new KeyPart { Number = x.Group }))
            {
                col.Find(x => x.Group == group.Key.Number).Select(x => x.Id).OrderBy(x => x)
                    .Should().Equal(rows.Where(x => x.Group == group.Key.Number).Select(x => x.Id));
            }
        }

        [Fact]
        public void Query_grouping_parameter_keeps_its_server_key_binding()
        {
            var mapper = new BsonMapper();
            var expression = mapper.GetExpression<IGrouping<int, Row>, int>(group => group.Key);
            expression.Source.Should().Be("@key");
        }

        [Fact]
        public void Captured_grouping_key_matches_each_CLR_group_in_both_orders()
        {
            var rows = Enumerable.Range(1, 7).Select(i => new Row { Id = i, Group = i % 3 }).ToArray();
            using var db = new LiteDatabase(":memory:");
            var col = db.GetCollection<Row>();
            col.Insert(rows);
            foreach (var group in rows.GroupBy(x => x.Group).Concat(rows.AsEnumerable().Reverse().GroupBy(x => x.Group)))
            {
                col.Find(x => x.Group == group.Key).Select(x => x.Id).OrderBy(x => x)
                    .Should().Equal(group.Select(x => x.Id).OrderBy(x => x));
            }
            col.Count().Should().Be(rows.Length);
        }
    }
}
