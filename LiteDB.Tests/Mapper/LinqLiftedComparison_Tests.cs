using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using FluentAssertions;
using Xunit;

namespace LiteDB.Tests.Mapper
{
    public class LinqLiftedComparison_Tests
    {
        public static IEnumerable<object[]> Predicates()
        {
            foreach (var operation in new[] { ExpressionType.LessThan, ExpressionType.LessThanOrEqual,
                ExpressionType.GreaterThan, ExpressionType.GreaterThanOrEqual, ExpressionType.Equal, ExpressionType.NotEqual })
            {
                var row = Expression.Parameter(typeof(Row), "x");
                var left = Expression.Property(row, nameof(Row.N));
                foreach (var right in new Expression[] { Expression.Property(row, nameof(Row.Other)),
                    Expression.Constant((int?)5, typeof(int?)), Expression.Constant(null, typeof(int?)) })
                {
                    yield return new object[] { Expression.Lambda<Func<Row, bool>>(Expression.MakeBinary(operation, left, right), row) };
                    yield return new object[] { Expression.Lambda<Func<Row, bool>>(Expression.MakeBinary(operation, right, left), row) };
                }
            }
            foreach (Expression<Func<Row, bool>> predicate in new Expression<Func<Row, bool>>[]
            {
                x => -x.N < 0, x => x.N < 5 && x.Other >= 5, x => x.N < 5 || x.Other > 5,
                x => !(x.N < 5), x => x.N == null || x.N > 5, x => x.N != null && x.N <= 5,
                x => (x.N < 5 || x.N > 5) && (x.Other == null || x.Other >= 5)
            }) yield return new object[] { predicate };
        }

        [Theory]
        [MemberData(nameof(Predicates))]
        public void Lifted_comparisons_match_CLR_for_scan_index_and_direct_execution(Expression<Func<Row, bool>> predicate)
        {
            var mapper = new BsonMapper();
            using var db = new LiteDatabase(":memory:", mapper);
            var rows = db.GetCollection<Row>();
            var values = new int?[] { null, -1, 0, 5, 6 };
            var id = 0;
            rows.Insert((from left in values from right in values select new Row { Id = ++id, N = left, Other = right }).ToArray());
            var stored = rows.FindAll().ToArray();
            var expected = stored.Where(predicate.Compile()).Select(x => x.Id).ToArray();
            for (var indexed = 0; indexed < 2; indexed++)
            {
                for (var repeat = 0; repeat < 3; repeat++)
                    rows.Find(predicate).Select(x => x.Id).Should().BeEquivalentTo(expected, predicate.ToString());
                rows.EnsureIndex(x => x.N);
                rows.EnsureIndex(x => x.Other);
            }
            using (new DirectTranslationScope())
            {
                var translated = mapper.GetExpression(predicate);
                stored.Where(x => translated.ExecuteScalar(mapper.ToDocument(x)).AsBoolean)
                    .Select(x => x.Id).Should().BeEquivalentTo(expected, predicate.ToString());
            }
        }

        [Fact]
        public void Captured_nullable_bounds_rebind_null_value_null_on_cache_hits()
        {
            var mapper = new BsonMapper();
            int? bound = null;
            Expression<Func<Row, bool>> predicate = x => x.N < bound || x.Other >= bound;
            var data = new[] { new Row { N = null, Other = null }, new Row { N = 1, Other = 6 }, new Row { N = 7, Other = null } };
            foreach (var value in new int?[] { null, 5, null })
            {
                bound = value;
                var translated = mapper.GetExpression(predicate);
                foreach (var row in data)
                    translated.ExecuteScalar(mapper.ToDocument(row)).AsBoolean.Should().Be(predicate.Compile()(row));
            }
        }

        [Fact]
        public void Raw_BSON_and_SQL_keep_total_order_null_comparisons()
        {
            BsonExpression.Create("N < 5").ExecuteScalar(new BsonDocument { ["N"] = BsonValue.Null }).AsBoolean.Should().BeTrue();
            using var db = new LiteDatabase(":memory:");
            db.GetCollection("rows").Insert(new BsonDocument { ["N"] = BsonValue.Null });
            using var result = db.Execute("SELECT $ FROM rows WHERE N < 5");
            result.Read().Should().BeTrue();
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void Explicit_lift_to_null_projections_match_CLR(bool computed)
        {
            var mapper = new BsonMapper();
            var row = Expression.Parameter(typeof(Row), "x");
            Expression left = Expression.Property(row, nameof(Row.N));
            if (computed) left = Expression.Negate(left);
            var right = Expression.Property(row, nameof(Row.Other));
            foreach (var operation in new[] { ExpressionType.LessThan, ExpressionType.LessThanOrEqual,
                ExpressionType.GreaterThan, ExpressionType.GreaterThanOrEqual, ExpressionType.Equal, ExpressionType.NotEqual })
            {
                var selector = Expression.Lambda<Func<Row, bool?>>(Expression.MakeBinary(operation, left, right, true, null), row);
                foreach (var n in new int?[] { null, -1, 0, 5 })
                foreach (var other in new int?[] { null, -1, 0, 5 })
                {
                    var value = new Row { N = n, Other = other };
                    var expected = selector.Compile()(value);
                    var actual = mapper.GetExpression(selector).ExecuteScalar(mapper.ToDocument(value));
                    if (expected == null) actual.IsNull.Should().BeTrue();
                    else actual.AsBoolean.Should().Be(expected.Value);
                }
            }
        }

        [Fact]
        public void Lifted_null_guards_preserve_BSON_arithmetic_errors_and_outer_short_circuiting()
        {
            var mapper = new BsonMapper();
            var value = new Row { N = null, D = 1m, Zero = 0m };
            var doc = mapper.ToDocument(value);
            Expression<Func<Row, bool>> predicate = x => (decimal?)x.N < x.D / x.Zero;
            Action bson = () => BsonExpression.Create("N < D / Zero").ExecuteScalar(doc);
            Action translated = () => mapper.GetExpression(predicate).ExecuteScalar(doc);
            bson.Should().Throw<DivideByZeroException>();
            translated.Should().Throw<DivideByZeroException>();
            mapper.GetExpression<Row, bool>(x => x.N == null || (decimal?)x.N < x.D / x.Zero)
                .ExecuteScalar(doc).AsBoolean.Should().BeTrue();
            mapper.GetExpression<Row, bool>(x => x.N != null && (decimal?)x.N < x.D / x.Zero)
                .ExecuteScalar(doc).AsBoolean.Should().BeFalse();
        }

        [Fact]
        public void Lifted_arithmetic_still_propagates_null()
        {
            var mapper = new BsonMapper();
            foreach (var n in new int?[] { null, 1, 5 })
            {
                var row = new Row { N = n, Other = 2 };
                var translated = mapper.GetExpression<Row, int?>(x => (x.N + x.Other) * x.N);
                var actual = translated.ExecuteScalar(mapper.ToDocument(row));
                if (n == null) actual.IsNull.Should().BeTrue();
                else actual.AsInt32.Should().Be((n.Value + 2) * n.Value);
            }
        }

        [Fact]
        public void Indexed_nullable_predicates_preserve_unrelated_rows_through_mutation_and_reopen()
        {
            using var file = new TempFile();
            using (var db = new LiteDatabase(file.Filename))
            {
                var rows = db.GetCollection<Row>("rows");
                rows.Insert(new[] { new Row { Id = 1, N = null, Other = 99 }, new Row { Id = 2, N = 1 },
                    new Row { Id = 3, N = 5 }, new Row { Id = 4, N = 6 } });
                rows.EnsureIndex(x => x.N);
                rows.Query().Where(x => x.N < 5).GetPlan()["index"]["name"].AsString.Should().Be("N");
                rows.Count(x => x.N < 5).Should().Be(1);
                rows.UpdateMany(x => new Row { Other = 7 }, x => x.N < 5).Should().Be(1);
                rows.FindById(1).Other.Should().Be(99);
                rows.FindById(2).Other.Should().Be(7);
                rows.DeleteMany(x => x.N < 5).Should().Be(1);
            }
            using (var db = new LiteDatabase(file.Filename))
            {
                var rows = db.GetCollection<Row>("rows");
                rows.FindAll().Select(x => x.Id).Should().BeEquivalentTo(new[] { 1, 3, 4 });
                rows.FindById(1).N.Should().BeNull();
                rows.FindById(1).Other.Should().Be(99);
                rows.Find(x => x.N >= 5).Select(x => x.Id).Should().BeEquivalentTo(new[] { 3, 4 });
            }
        }

        [Fact]
        public void Nullable_runtime_operand_is_evaluated_once_and_source_roundtrips()
        {
            var mapper = new BsonMapper();
            var translated = mapper.GetExpression<Row, bool>(x => x.Stamp < (DateTime?)DateTime.Now);
            var calls = new ClockCalls();
            calls.Visit(translated.Expression);
            calls.Count.Should().Be(1);
            translated.IsVolatile.Should().BeTrue();
            var parsed = BsonExpression.Create(translated.Source, translated.Parameters);
            foreach (var stamp in new DateTime?[] { null, new DateTime(2000, 1, 1), new DateTime(2100, 1, 1) })
            {
                var doc = mapper.ToDocument(new Row { Stamp = stamp });
                translated.ExecuteScalar(doc).AsBoolean.Should().Be(stamp < DateTime.Now);
                parsed.ExecuteScalar(doc).Should().Be(translated.ExecuteScalar(doc));
            }
        }

        private sealed class ClockCalls : ExpressionVisitor
        {
            internal int Count;
            protected override Expression VisitMethodCall(MethodCallExpression node)
            {
                if (node.Method.Name == "NOW") Count++;
                return base.VisitMethodCall(node);
            }
        }

        public class Row
        {
            public int Id { get; set; }
            public int? N { get; set; }
            public int? Other { get; set; }
            public decimal D { get; set; }
            public decimal Zero { get; set; }
            public DateTime? Stamp { get; set; }
        }
    }
}
