using System.Linq;
using FluentAssertions;
using Xunit;

namespace LiteDB.Tests.Issues
{
    public class Issue1715_Tests
    {
        public class Row
        {
            public int Id { get; set; }
            public int A { get; set; }
            public int B { get; set; }
        }

        [Fact]
        public void Composed_predicates_keep_independent_parameters_and_truth_tables()
        {
            using var db = new LiteDatabase(":memory:");
            var col = db.GetCollection<Row>();
            var rows = new[]
            {
                new Row { Id = 1, A = 1, B = 2 }, new Row { Id = 2, A = 2, B = 1 },
                new Row { Id = 3, A = 1, B = 1 }, new Row { Id = 4, A = 2, B = 2 }
            };
            col.Insert(rows);
            var mapper = new BsonMapper();
            var left = mapper.GetExpression<Row, bool>(x => x.A == 1);
            var right = mapper.GetExpression<Row, bool>(x => x.B == 2);
            col.Find(left).Select(x => x.Id).OrderBy(x => x).Should().Equal(1, 3);
            col.Find(right).Select(x => x.Id).OrderBy(x => x).Should().Equal(1, 4);
            col.Find(Query.And(left, right)).Select(x => x.Id).Should().Equal(1);
            col.Find(Query.And(right, left)).Select(x => x.Id).Should().Equal(1);
            col.Find(Query.Or(left, right)).Select(x => x.Id).OrderBy(x => x).Should().Equal(1, 3, 4);
            // Composition must not change either input expression's meaning.
            col.Find(left).Select(x => x.Id).OrderBy(x => x).Should().Equal(1, 3);
            col.Find(right).Select(x => x.Id).OrderBy(x => x).Should().Equal(1, 4);
        }
    }
}
