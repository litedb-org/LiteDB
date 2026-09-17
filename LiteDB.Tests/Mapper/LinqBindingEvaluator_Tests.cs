using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using FluentAssertions;
using Xunit;

namespace LiteDB.Tests.Mapper
{
    public class LinqBindingEvaluator_Tests
    {
        [Fact]
        public void Helper_calls_use_current_closures_and_live_serializers()
        {
            var mapper = new BsonMapper();
            var first = mapper.GetExpression(Point(new Provider { Value = 3 }));
            var second = mapper.GetExpression(Point(new Provider { Value = 7 }));
            second.Expression.Should().BeSameAs(first.Expression);
            first.Parameters["p0"].AsInt32.Should().Be(3);
            second.Parameters["p0"].AsInt32.Should().Be(7);
            mapper.RegisterType<int>(value => value + 100, value => value.AsInt32 - 100);
            mapper.GetExpression(Point(new Provider { Value = 9 })).Parameters["p0"].AsInt32.Should().Be(109);
        }

        [Fact]
        public void Repeated_helper_calls_keep_evaluation_order_and_count()
        {
            var mapper = new BsonMapper();
            var provider = new Provider();
            Expression<Func<Row, bool>> query = x => x.Id >= provider.Next() && x.Id < provider.Next();
            for (var i = 0; i < 3; i++)
            {
                var bound = mapper.GetExpression(query);
                bound.Parameters["p0"].AsInt32.Should().Be(i * 2 + 1);
                bound.Parameters["p1"].AsInt32.Should().Be(i * 2 + 2);
            }
            provider.Value.Should().Be(6);
        }

        [Fact]
        public void Constants_are_bound_by_occurrence_even_when_first_tree_shares_nodes()
        {
            var mapper = new BsonMapper();
            var same = Expression.Constant(2);
            var first = mapper.GetExpression(Construct(same, same));
            var second = mapper.GetExpression(Construct(Expression.Constant(3), Expression.Constant(7)));
            second.Expression.Should().BeSameAs(first.Expression);
            first.Parameters["p0"].AsInt32.Should().Be(22);
            second.Parameters["p0"].AsInt32.Should().Be(37);
        }

        [Fact]
        public void Initializers_and_null_static_targets_preserve_shape_positions()
        {
            var mapper = new BsonMapper();
            BsonExpression previous = null;
            for (var i = 1; i < 4; i++)
            {
                var value = i;
                Expression<Func<Row, bool>> query = x => x.Id == Read(new Provider { Value = value }, 10);
                var bound = mapper.GetExpression(query);
                if (previous != null) bound.Expression.Should().BeSameAs(previous.Expression);
                bound.Parameters["p0"].AsInt32.Should().Be(i + 10);
                previous = bound;
            }
        }

        [Fact]
        public void Reused_binding_nodes_do_not_alias_values_in_future_trees()
        {
            var mapper = new BsonMapper();
            var row = Expression.Parameter(typeof(Row), "x");
            var id = Expression.Property(row, nameof(Row.Id));
            var same = Expression.Constant(2);
            Expression<Func<Row, bool>> Query(Expression left, Expression right) =>
                Expression.Lambda<Func<Row, bool>>(Expression.AndAlso(Expression.GreaterThan(id, left), Expression.LessThan(id, right)), row);
            mapper.GetExpression(Query(same, same));
            var next = mapper.GetExpression(Query(Expression.Constant(3), Expression.Constant(7)));
            next.Parameters["p0"].AsInt32.Should().Be(3);
            next.Parameters["p1"].AsInt32.Should().Be(7);
        }

        [Fact]
        public void Cached_helpers_preserve_nullable_results()
        {
            var mapper = new BsonMapper();
            var provider = new Provider();
            Expression<Func<Row, bool>> query = x => x.Id == provider.Nullable();
            mapper.GetExpression(query).Parameters["p0"].AsInt32.Should().Be(0);
            provider.ReturnNull = true;
            mapper.GetExpression(query).Parameters["p0"].IsNull.Should().BeTrue();
            provider.ReturnNull = false;
            provider.Value = 7;
            mapper.GetExpression(query).Parameters["p0"].AsInt32.Should().Be(7);
        }

        [Fact]
        public void Nested_member_initializers_keep_uncached_semantics()
        {
            var mapper = new BsonMapper();
            Expression<Func<Row, bool>> first = x => x.Id == ReadHolder(new Holder { First = { Value = 3 } });
            Expression<Func<Row, bool>> second = x => x.Id == ReadHolder(new Holder { Second = { Value = 7 } });
            mapper.GetExpression(first).Parameters["p0"].AsInt32.Should().Be(30);
            mapper.GetExpression(second).Parameters["p0"].AsInt32.Should().Be(7);
        }

        [Theory]
        [InlineData(0)]
        [InlineData(1)]
        [InlineData(2)]
        [InlineData(3)]
        public void Cached_method_and_member_failures_match_uncached_exception_chains(int mode)
        {
            var provider = new Provider { Value = 1 };
            Expression<Func<Row, bool>> query = mode == 0 ? x => x.Id == provider.Get() :
                mode == 1 ? x => x.Id == provider.Self().Value :
                mode == 2 ? x => x.Id == provider.Self().Property : x => x.Id == provider.Self().Field;
            var mapper = new BsonMapper();
            mapper.GetExpression(query);
            mapper.GetExpression(query);
            provider.Fail = mode == 0 || mode == 2;
            provider.ReturnNull = mode == 1 || mode == 3;
            var cached = Record.Exception(() => mapper.GetExpression(query));
            var uncached = Record.Exception(() => new BsonMapper().GetExpression(query));
            cached.Should().BeOfType<NotSupportedException>();
            Chain(cached).Should().Equal(Chain(uncached));
        }

        [Fact]
        public void Helper_calls_support_concurrency_and_reentrancy()
        {
            var mapper = new BsonMapper();
            Parallel.For(0, 100, i =>
            {
                var provider = new Provider { Value = i, Reenter = () => mapper.GetExpression(Point(new Provider { Value = 99 })) };
                mapper.GetExpression(Point(provider)).Parameters["p0"].AsInt32.Should().Be(i);
            });
        }

        [Fact]
        public void Compiled_evaluators_do_not_retain_provider_or_closure_objects()
        {
            var mapper = new BsonMapper();
            var references = Temporary(mapper).Concat(Temporary(mapper)).ToArray();
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            references.Should().OnlyContain(reference => !reference.IsAlive);
            GC.KeepAlive(mapper);
        }

        [Fact]
        public void Queries_execute_with_changing_helper_results_and_member_targets()
        {
            using var db = new LiteDatabase(":memory:");
            var rows = db.GetCollection<Row>();
            rows.InsertBulk(Enumerable.Range(1, 20).Select(i => new Row { Id = i }));
            var provider = new Provider();
            for (var i = 1; i <= 20; i++)
            {
                provider.Value = i;
                rows.Query().Where(x => x.Id == provider.Get()).Single().Id.Should().Be(i);
                rows.Query().Where(x => x.Id == provider.Self().Property).Single().Id.Should().Be(i);
            }
        }

        private static IEnumerable<Type> Chain(Exception error)
        {
            while (error != null) { yield return error.GetType(); error = error.InnerException; }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static WeakReference[] Temporary(BsonMapper mapper)
        {
            var provider = new Provider { Value = 7 };
            var query = Point(provider);
            mapper.GetExpression(query);
            var call = (MethodCallExpression)((BinaryExpression)query.Body).Right;
            var closure = (ConstantExpression)((MemberExpression)call.Object).Expression;
            return new[] { new WeakReference(provider), new WeakReference(closure.Value) };
        }

        private static Expression<Func<Row, bool>> Point(Provider provider) => x => x.Id == provider.Get();
        private static int Read(Provider provider, int value) => provider.Value + value;
        private static int ReadHolder(Holder holder) => holder.First.Value * 10 + holder.Second.Value;
        public static int Combine(int left, int right) => left * 10 + right;
        private static Expression<Func<Row, bool>> Construct(Expression left, Expression right)
        {
            var row = Expression.Parameter(typeof(Row), "x");
            return Expression.Lambda<Func<Row, bool>>(Expression.Equal(Expression.Property(row, nameof(Row.Id)),
                Expression.Call(typeof(LinqBindingEvaluator_Tests).GetMethod(nameof(Combine)), left, right)), row);
        }

        public class Row { public int Id { get; set; } }
        private class Holder
        {
            public Provider First { get; } = new Provider();
            public Provider Second { get; } = new Provider();
        }
        private class Provider
        {
            public int Value { get; set; }
            public int Field = 1;
            public bool Fail;
            public bool ReturnNull;
            public Action Reenter;
            public int Property => Fail ? throw new InvalidOperationException("getter") : Value;
            public int Next() => ++Value;
            public Provider Self() => ReturnNull ? null : this;
            public int? Nullable() => ReturnNull ? (int?)null : Value;
            public int Get()
            {
                Reenter?.Invoke();
                if (Fail) throw new InvalidOperationException("helper");
                return Value;
            }
        }
    }
}
