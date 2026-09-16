using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using FluentAssertions;
using Xunit;

namespace LiteDB.Tests.Issues
{
    public class Issue2810_Quantifier_Tests
    {
        public class Row
        {
            public int Id { get; set; }
            public int Threshold { get; set; }
            public int[] Numbers { get; set; }
            public bool[] Flags { get; set; }
            public List<int[]> Groups { get; set; }
        }

        [Fact]
        public void General_quantifiers_match_CLR_for_root_references_nested_sequences_and_empty_inputs()
        {
            var rows = new[]
            {
                new Row { Id = 1, Threshold = 2, Numbers = new[] { 1, 4 }, Flags = new[] { false, true },
                    Groups = new List<int[]> { new[] { 1 }, new[] { 4 } } },
                new Row { Id = 2, Threshold = 9, Numbers = new[] { 1, 4 }, Flags = new[] { false },
                    Groups = new List<int[]> { new[] { 1, 4 } } },
                new Row { Id = 3, Threshold = 0, Numbers = new int[0], Flags = new bool[0],
                    Groups = new List<int[]>() }
            };
            using var db = new LiteDatabase(":memory:");
            var col = db.GetCollection<Row>();
            col.Insert(rows);
            AssertQuery(col, rows, x => x.Numbers.Any(n => n > 2 && n > x.Threshold));
            AssertQuery(col, rows, x => x.Numbers.All(n => n <= 2 || n > x.Threshold));
            AssertQuery(col, rows, x => x.Numbers.Any(n => n == n + 0));
            AssertQuery(col, rows, x => x.Numbers.All(n => n == n + 0));
            AssertQuery(col, rows, x => x.Flags.Any(flag => flag));
            AssertQuery(col, rows, x => x.Flags.All(flag => !flag));
            AssertQuery(col, rows, x => x.Groups.Any(group => group.Contains(4)));
            AssertQuery(col, rows, x => x.Groups.All(group => group.Contains(4)));
            AssertQuery(col, rows, x => x.Groups.Any(group => group.Any(n => n > x.Threshold)));
            AssertQuery(col, rows, x => x.Groups.All(group => group.All(n => n < x.Threshold)));
        }

        [Fact]
        public void Unsupported_outer_item_capture_is_rejected_instead_of_rebound_to_the_inner_item()
        {
            var mapper = new BsonMapper();
            Action translate = () => mapper.GetExpression<Row, bool>(
                x => x.Groups.Any(group => group.Any(n => n == group[0])));
            translate.Should().Throw<NotSupportedException>().WithMessage("*outer collection item*");
        }

        private static void AssertQuery(ILiteCollection<Row> col, Row[] rows, Expression<Func<Row, bool>> predicate)
        {
            col.Find(predicate).Select(x => x.Id).OrderBy(x => x)
                .Should().Equal(rows.Where(predicate.Compile()).Select(x => x.Id));
        }
    }
}
