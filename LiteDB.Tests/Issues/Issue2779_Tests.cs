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
            public int MobileId { get; set; }
        }

        public class Stock
        {
            public int[] ContactIds { get; set; }
        }

        private sealed class ClientEvaluator
        {
            public int Calls { get; private set; }

            public T Evaluate<T>(T value)
            {
                Calls++;

                return value;
            }
        }

        [Fact]
        public void Closed_round_overloads_use_CLR_and_unsupported_row_overloads_reject()
        {
            using var db = new LiteDatabase(":memory:");
            var col = db.GetCollection<Row>();
            col.Insert(new[] { new Row { Id = 2, MobileId = 2 }, new Row { Id = 3, MobileId = 3 } });
            var captured = 2.5;
            col.Find(x => x.MobileId == Math.Round(captured)).Select(x => x.Id).Should().Equal(2);
            col.Find(x => x.MobileId == Math.Round(captured, MidpointRounding.AwayFromZero))
                .Select(x => x.Id).Should().Equal(3);
            col.Find(x => x.MobileId == Math.Round(captured, 0, MidpointRounding.ToEven))
                .Select(x => x.Id).Should().Equal(2);
            Action dependent = () => db.Mapper.GetExpression<Row, bool>(x => Math.Round((double)x.MobileId) == 2);
            Action roundingMode = () => db.Mapper.GetExpression<Row, bool>(x =>
                Math.Round((double)x.MobileId, MidpointRounding.AwayFromZero) == 2);
            dependent.Should().Throw<NotSupportedException>();
            roundingMode.Should().Throw<NotSupportedException>();
        }

        [Fact]
        public void Original_selectmany_distinct_contains_is_one_parameter_and_matches_id_ledger()
        {
            var allstocks = new[]
            {
                new Stock { ContactIds = new[] { 99, 7, 99 } },
                new Stock { ContactIds = new[] { 42, 777, 42 } }
            };
            Expression<Func<Row, bool>> predicate = e => allstocks
                .SelectMany(x => x.ContactIds)
                .Distinct()
                .Contains(e.MobileId);

            using var db = new LiteDatabase(new ConnectionString
            {
                Filename = ":memory:",
                Collation = Collation.Binary
            });
            var col = db.GetCollection<Row>();
            col.Insert(new[]
            {
                new Row { Id = 11, MobileId = 7 },
                new Row { Id = 22, MobileId = 42 },
                new Row { Id = 33, MobileId = 99 },
                new Row { Id = 44, MobileId = 123 }
            });

            var expression = db.Mapper.GetExpression(predicate);
            expression.Source.Should().Be("ITEMS(@p0) ANY=$.MobileId");
            expression.Parameters.Keys.Should().Equal("p0");
            expression.Parameters["p0"].AsArray.Select(x => x.AsInt32)
                .Should().Equal(99, 7, 42, 777);

            col.Query().Where(predicate).OrderBy(x => x.Id).Select(x => x.Id).ToArray()
                .Should().Equal(new[] { 11, 22, 33 },
                    "these ids are a hand-written ledger, independent of compiling the query predicate");
            col.Count().Should().Be(4, "a filter must not mutate or discard its non-matching row");

            var plan = col.Query().Where(predicate).GetPlan();
            var filters = plan["filters"].AsArray;
            filters.Count.Should().Be(1);
            filters[0].AsString.Should().Be("$.MobileId IN ARRAY(ITEMS(@p0))",
                "the captured computation must reach the engine as one parameter, not as server-side SelectMany or Distinct calls");
        }

        [Fact]
        public void Precomputed_contains_control_uses_the_same_parameter_and_id_ledger()
        {
            var contactIds = new[] { 99, 7, 42, 777 };
            Expression<Func<Row, bool>> predicate = e => contactIds.Contains(e.MobileId);

            using var db = new LiteDatabase(":memory:");
            var col = db.GetCollection<Row>();
            col.Insert(new[]
            {
                new Row { Id = 11, MobileId = 7 },
                new Row { Id = 22, MobileId = 42 },
                new Row { Id = 33, MobileId = 99 },
                new Row { Id = 44, MobileId = 123 }
            });

            var expression = db.Mapper.GetExpression(predicate);
            expression.Source.Should().Be("ITEMS(@p0) ANY=$.MobileId");
            expression.Parameters.Keys.Should().Equal("p0");
            expression.Parameters["p0"].AsArray.Select(x => x.AsInt32)
                .Should().Equal(99, 7, 42, 777);
            col.Find(predicate).Select(x => x.Id).OrderBy(x => x)
                .Should().Equal(11, 22, 33);

            var plan = col.Query().Where(predicate).GetPlan();
            plan["filters"].AsArray.Count.Should().Be(1);
            plan["filters"][0].AsString.Should().Be("$.MobileId IN ARRAY(ITEMS(@p0))");
        }

        [Fact]
        public void Generic_client_evaluation_and_parameter_dependent_resolver_calls_keep_their_boundaries()
        {
            using var db = new LiteDatabase(new ConnectionString
            {
                Filename = ":memory:",
                Collation = Collation.Binary
            });
            var col = db.GetCollection<Row>();
            col.Insert(new[]
            {
                new Row { Id = 1, Name = "a.b" },
                new Row { Id = 2, Name = "A.B" },
                new Row { Id = 3, Name = "other" }
            });
            var evaluator = new ClientEvaluator();
            var first = "a";
            var second = "b";
            Expression<Func<Row, bool>> captured = x =>
                x.Name == evaluator.Evaluate(string.Concat(first, ".", second));

            var capturedExpression = db.Mapper.GetExpression(captured);
            capturedExpression.Source.Should().Be("($.Name=@p0)");
            capturedExpression.Parameters.Keys.Should().Equal("p0");
            capturedExpression.Parameters["p0"].AsString.Should().Be("a.b");
            evaluator.Calls.Should().Be(1, "a parameter-independent call is evaluated once while translating the query");
            col.Find(capturedExpression).Select(x => x.Id).Should().Equal(1);
            evaluator.Calls.Should().Be(1, "the engine must consume the parameter instead of invoking client code per row");

            var supported = db.Mapper.GetExpression<Row, bool>(x => x.Name.ToUpper() == "A.B");
            supported.Source.Should().Be("(UPPER($.Name)=@p0)");
            col.Find(supported).Select(x => x.Id).OrderBy(x => x).Should().Equal(1, 2);

            Action parameterDependent = () => db.Mapper.GetExpression<Row, bool>(x => evaluator.Evaluate(x.Name) == "a.b");
            parameterDependent.Should().Throw<NotSupportedException>(
                "client evaluation must never execute a method that depends on the document parameter");
            evaluator.Calls.Should().Be(1, "rejection must happen before document-dependent client code is invoked");

            Action nestedCapture = () => db.Mapper.GetExpression<Row, bool>(x =>
                evaluator.Evaluate(new[] { 1 }.Select(unused => x.Name).First()) == "a.b");
            nestedCapture.Should().Throw<NotSupportedException>();
            evaluator.Calls.Should().Be(1, "a nested lambda must not hide a captured row dependency");
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
