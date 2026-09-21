using System;
using System.Linq;
using System.Linq.Expressions;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using FluentAssertions;
using Xunit;

namespace LiteDB.Tests.Mapper
{
    public class LinqExpressionCache_Tests
    {
        [Fact]
        public void New_closures_reuse_ir_with_independent_current_values_and_metadata()
        {
            var mapper = new BsonMapper();
            var first = mapper.GetExpression(Point(3));
            first.Parameters["p0"] = 999;
            first.Fields.Clear();
            var second = mapper.GetExpression(Point(7));
            second.Expression.Should().BeSameAs(first.Expression);
            second.Parameters["p0"].AsInt32.Should().Be(7);
            second.Fields.Should().Contain("Score");
            second.ExecuteScalar(new BsonDocument { ["Score"] = 7 }).AsBoolean.Should().BeTrue();
            first.Parameters["p0"].AsInt32.Should().Be(999);
        }

        [Fact]
        public void Nested_lambdas_and_repeated_parameter_slots_rebind()
        {
            var mapper = new BsonMapper();
            var first = mapper.GetExpression(Nested(2));
            var second = mapper.GetExpression(Nested(5));
            second.Expression.Should().BeSameAs(first.Expression);
            var document = new BsonDocument { ["Values"] = new BsonArray(1, 4, 7) };
            first.ExecuteScalar(document).AsArray.Select(x => x.AsInt32).Should().Equal(6, 9);
            second.ExecuteScalar(document).AsArray.Select(x => x.AsInt32).Should().Equal(12);
        }

        [Fact]
        public void Mutable_mapper_metadata_invalidates_templates_and_serializers_are_live()
        {
            var mapper = new BsonMapper();
            mapper.GetExpression(Point(3));
            mapper.Entity<Row>().Field(x => x.Score, "points");
            mapper.GetExpression(Point(4)).Source.Should().Contain("$.points");
            mapper.RegisterType<int>(value => value + 100, value => value.AsInt32 - 100);
            mapper.GetExpression(Point(4)).Parameters["p0"].AsInt32.Should().Be(104);
            var member = mapper.GetEntityMapper(typeof(Row)).Members.Single(x => x.MemberName == "Score");
            member.FieldName = "changed";
            mapper.GetExpression(Point(4)).Source.Should().Contain("$.changed");
            mapper.GetEntityMapper(typeof(Row)).Members.Remove(member);
            Action translate = () => mapper.GetExpression(Point(4));
            translate.Should().Throw<NotSupportedException>();
        }

        [Fact]
        public void Mapper_instances_and_boolean_index_translation_are_isolated()
        {
            var first = new BsonMapper();
            var second = new BsonMapper();
            second.Entity<Row>().Field(x => x.Score, "other");
            first.GetExpression(Point(1)).Source.Should().Contain("$.Score");
            second.GetExpression(Point(1)).Source.Should().Contain("$.other");
            Expression<Func<Row, bool>> flag = x => x.Enabled;
            first.GetExpression(flag).Source.Should().Be("($.Enabled=true)");
            first.GetIndexExpression(flag).Source.Should().Be("$.Enabled");
            first.GetExpression(flag).Source.Should().Be("($.Enabled=true)");
        }

        [Fact]
        public void Structural_index_arguments_and_enum_settings_keep_existing_semantics()
        {
            var mapper = new BsonMapper();
            var index = 0;
            Expression<Func<Row, int>> item = x => x.Values[index];
            mapper.GetExpression(item).Source.Should().Be("$.Values[0]");
            index = 1;
            mapper.GetExpression(item).Source.Should().Be("$.Values[1]");
            Expression<Func<Row, bool>> state = x => x.State == State.Ready;
            mapper.GetExpression(state).Parameters["p0"].AsString.Should().Be("Ready");
            mapper.EnumAsInteger = true;
            mapper.GetExpression(state).Parameters["p0"].AsInt32.Should().Be((int)State.Ready);
            mapper.GetExpression(state).Parameters["p0"].AsInt32.Should().Be((int)State.Ready);
            mapper.EnumAsInteger = false;
            mapper.GetExpression(state).Parameters["p0"].AsString.Should().Be("Ready");
        }

        [Fact]
        public void Enum_equals_with_changing_captured_runtime_types_is_not_cached_as_false()
        {
            var mapper = new BsonMapper();
            object value = null;
            Expression<Func<Row, bool>> predicate = x => x.State.Equals(value);
            var document = new BsonDocument { ["State"] = "Ready" };
            mapper.GetExpression(predicate).ExecuteScalar(document).AsBoolean.Should().BeFalse();
            value = "Ready";
            mapper.GetExpression(predicate).ExecuteScalar(document).AsBoolean.Should().BeFalse();
            value = State.Ready;
            mapper.GetExpression(predicate).ExecuteScalar(document).AsBoolean.Should().BeTrue();
        }

        [Fact]
        public void Captured_values_that_select_the_translation_are_reread_on_every_call()
        {
            // These branches evaluate a captured value while choosing the IR, so a
            // published template would freeze the first value.
            var mapper = new BsonMapper();
            var document = new BsonDocument { ["State"] = "Ready", ["Tags"] = new BsonDocument { ["a"] = 1, ["b"] = 2 } };
            var state = State.New;
            Expression<Func<Row, bool>> byState = x => x.State == state;
            Expression<Func<Row, bool>> reversed = x => state == x.State;
            mapper.GetExpression(byState).ExecuteScalar(document).AsBoolean.Should().BeFalse();
            mapper.GetExpression(reversed).ExecuteScalar(document).AsBoolean.Should().BeFalse();
            state = State.Ready;
            mapper.GetExpression(byState).ExecuteScalar(document).AsBoolean.Should().BeTrue();
            mapper.GetExpression(reversed).ExecuteScalar(document).AsBoolean.Should().BeTrue();

            var key = "a";
            Expression<Func<Row, int>> byKey = x => x.Tags[key];
            mapper.GetExpression(byKey).ExecuteScalar(document).AsInt32.Should().Be(1);
            key = "b";
            mapper.GetExpression(byKey).ExecuteScalar(document).AsInt32.Should().Be(2);

            // Ordinal adds a plain equality term; reusing that shape for another
            // mode would reject values that differ only by case under a binary collation.
            var mode = StringComparison.Ordinal;
            var names = new BsonDocument { ["Name"] = "READY" };
            Expression<Func<Row, bool>> byName = x => x.Name.Equals("ready", mode);
            mapper.GetExpression(byName).ExecuteScalar(names, Collation.Binary).AsBoolean.Should().BeFalse();
            mode = StringComparison.OrdinalIgnoreCase;
            mapper.GetExpression(byName).ExecuteScalar(names, Collation.Binary).AsBoolean.Should().BeTrue();
            mode = StringComparison.Ordinal;
            mapper.GetExpression(byName).ExecuteScalar(names, Collation.Binary).AsBoolean.Should().BeFalse();
        }

        [Fact]
        public void Captured_getters_keep_translation_order_and_evaluation_count()
        {
            var mapper = new BsonMapper();
            var counter = new Counter();
            Expression<Func<Row, bool>> predicate = x => x.Score >= counter.Next && x.Score < counter.Next;
            var first = mapper.GetExpression(predicate);
            var second = mapper.GetExpression(predicate);
            second.Expression.Should().BeSameAs(first.Expression);
            first.Parameters["p0"].AsInt32.Should().Be(1);
            first.Parameters["p1"].AsInt32.Should().Be(2);
            second.Parameters["p0"].AsInt32.Should().Be(3);
            second.Parameters["p1"].AsInt32.Should().Be(4);
        }

        [Fact]
        public void Concurrent_bindings_are_independent_and_cache_does_not_retain_closures()
        {
            var mapper = new BsonMapper();
            Parallel.For(0, 200, i =>
            {
                var expression = mapper.GetExpression(Point(i));
                expression.Parameters["p0"].AsInt32.Should().Be(i);
                expression.ExecuteScalar(new BsonDocument { ["Score"] = i }).AsBoolean.Should().BeTrue();
            });
            var weak = TranslateTemporaryClosure(mapper);
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            weak.IsAlive.Should().BeFalse();
            GC.KeepAlive(mapper);
        }

        [Fact]
        public void Captured_getters_can_reenter_translation_without_overwriting_the_outer_shape()
        {
            var mapper = new BsonMapper();
            var getter = new ReentrantGetter(mapper);
            Expression<Func<Row, bool>> predicate = x => x.Score == getter.Value;
            for (var i = 0; i < 3; i++)
                mapper.GetExpression(predicate).ExecuteScalar(new BsonDocument { ["Score"] = 7 }).AsBoolean.Should().BeTrue();
        }

        [Fact]
        public void Shape_cache_is_bounded_and_hash_collisions_do_not_change_results()
        {
            var mapper = new BsonMapper();
            var root = Expression.Parameter(typeof(Row), "x");
            var field = Expression.Property(root, nameof(Row.Score));
            for (var size = 1; size <= 300; size++)
            {
                var values = Expression.NewArrayInit(typeof(int), Enumerable.Repeat(field, size));
                var expression = Expression.Lambda<Func<Row, int[]>>(values, root);
                var result = mapper.GetExpression(expression).ExecuteScalar(new BsonDocument { ["Score"] = 7 }).AsArray;
                result.Count.Should().Be(size);
                result.All(x => x.AsInt32 == 7).Should().BeTrue();
            }
            mapper.LinqExpressionCacheCount.Should().BeInRange(1, 256);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static WeakReference TranslateTemporaryClosure(BsonMapper mapper)
        {
            var expression = Point(37);
            var closure = ((MemberExpression)((BinaryExpression)expression.Body).Right).Expression as ConstantExpression;
            mapper.GetExpression(expression);
            return new WeakReference(closure.Value);
        }

        private static Expression<Func<Row, bool>> Point(int value) => x => x.Score == value;
        private static Expression<Func<Row, int[]>> Nested(int value) => x => x.Values.Where(n => n > value).Select(n => n + value).ToArray();
        private class Counter
        {
            private int _value;
            public int Next => ++_value;
        }
        private class ReentrantGetter
        {
            private readonly BsonMapper _mapper;
            internal ReentrantGetter(BsonMapper mapper) { _mapper = mapper; }
            public int Value => _mapper.GetExpression(Point(7)).Parameters["p0"].AsInt32;
        }
        public enum State { New, Ready }
        public class Row
        {
            public int Id { get; set; }
            public int Score { get; set; }
            public int[] Values { get; set; }
            public bool Enabled { get; set; }
            public State State { get; set; }
            public string Name { get; set; }
            public System.Collections.Generic.Dictionary<string, int> Tags { get; set; }
        }
    }
}
