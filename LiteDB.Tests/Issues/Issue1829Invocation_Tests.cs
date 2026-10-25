using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using Xunit;

namespace LiteDB.Tests.Issues
{
    public class Issue1829Invocation_Tests
    {
        public class Row
        {
            public int Id { get; set; }
            public List<Row> Children { get; set; }
        }

        [Fact]
        public void Invocation_substitutes_multiple_arguments_and_member_access_arguments()
        {
            var root = Expression.Parameter(typeof(Row), "row");
            Expression<Func<Row, int, bool>> above = (value, minimum) => value.Id > minimum;
            Expression<Func<int, bool>> below = value => value < 7;
            var body = Expression.AndAlso(
                Expression.Invoke(above, root, Expression.Constant(5)),
                Expression.Invoke(below, Expression.Property(root, nameof(Row.Id))));
            var predicate = Expression.Lambda<Func<Row, bool>>(body, root);
            AssertQuery(predicate, new[] { new Row { Id = 4 }, new Row { Id = 6 }, new Row { Id = 7 } }, 6);
        }

        [Fact]
        public void Invocation_does_not_capture_a_free_argument_in_a_nested_lambda()
        {
            var root = Expression.Parameter(typeof(Row), "row");
            var invoked = Expression.Parameter(typeof(Row), "value");
            var inner = Expression.Lambda<Func<Row, bool>>(
                Expression.LessThan(Expression.Property(root, nameof(Row.Id)), Expression.Property(invoked, nameof(Row.Id))), root);
            var any = Expression.Call(typeof(Enumerable), nameof(Enumerable.Any), new[] { typeof(Row) },
                Expression.Property(invoked, nameof(Row.Children)), inner);
            var lambda = Expression.Lambda<Func<Row, bool>>(any, invoked);
            var predicate = Expression.Lambda<Func<Row, bool>>(Expression.Invoke(lambda, root), root);
            AssertQuery(predicate, new[]
            {
                new Row { Id = 2, Children = new List<Row> { new Row { Id = 1 } } },
                new Row { Id = 3, Children = new List<Row> { new Row { Id = 4 } } }
            }, 2);
        }

        [Fact]
        public void Nested_declarations_shadow_an_identical_invoked_parameter()
        {
            var root = Expression.Parameter(typeof(Row), "row");
            var invoked = Expression.Parameter(typeof(Row), "value");
            var inner = Expression.Lambda<Func<Row, bool>>(
                Expression.LessThan(Expression.Property(invoked, nameof(Row.Id)), Expression.Constant(5)), invoked);
            var any = Expression.Call(typeof(Enumerable), nameof(Enumerable.Any), new[] { typeof(Row) },
                Expression.Property(invoked, nameof(Row.Children)), inner);
            var lambda = Expression.Lambda<Func<Row, bool>>(any, invoked);
            var predicate = Expression.Lambda<Func<Row, bool>>(Expression.Invoke(lambda, root), root);
            AssertQuery(predicate, new[]
            {
                new Row { Id = 10, Children = new List<Row> { new Row { Id = 1 } } },
                new Row { Id = 1, Children = new List<Row> { new Row { Id = 10 } } }
            }, 10);
        }

        public class Holder { public Holder Child; public int Value; }

        [Fact]
        public void Captured_field_arguments_are_not_silently_omitted()
        {
            var holder = new Holder();
            var root = Expression.Parameter(typeof(Row), "row");
            Expression<Func<Row, int, bool>> ignore = (row, unused) => row.Id > 0;
            var field = Expression.Field(Expression.Field(Expression.Constant(holder), nameof(Holder.Child)), nameof(Holder.Value));
            var predicate = Expression.Lambda<Func<Row, bool>>(Expression.Invoke(ignore, root, field), root);
            Assert.Throws<NullReferenceException>(() => predicate.Compile()(new Row { Id = 1 }));
            Assert.Throws<NotSupportedException>(() => new BsonMapper().GetExpression(predicate));
        }

        public class Counter
        {
            public int Reads { get; private set; }
            public int Value => ++Reads;
            public int NextIndex() { Reads++; return 0; }
        }

        [Fact]
        public void Volatile_invocation_arguments_are_rejected_instead_of_duplicated()
        {
            var root = Expression.Parameter(typeof(Row), "row");
            Expression<Func<Guid, bool>> same = value => value == value;
            var predicate = Expression.Lambda<Func<Row, bool>>(Expression.Invoke(same,
                Expression.Call(typeof(Guid), nameof(Guid.NewGuid), null)), root);
            Assert.True(predicate.Compile()(new Row()));
            Assert.Throws<NotSupportedException>(() => new BsonMapper().GetExpression(predicate));
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void Captured_getters_are_not_duplicated_or_omitted_by_invocation_expansion(bool unused)
        {
            var counter = new Counter();
            var root = Expression.Parameter(typeof(Row), "row");
            Expression<Func<int, bool>> same = value => value == value;
            Expression<Func<int, bool>> ignored = value => true;
            var predicate = Expression.Lambda<Func<Row, bool>>(Expression.Invoke(unused ? ignored : same,
                Expression.Property(Expression.Constant(counter), nameof(Counter.Value))), root);
            Assert.True(predicate.Compile()(new Row()));
            Assert.Equal(1, counter.Reads);
            var expression = new BsonMapper().GetExpression(predicate);
            Assert.True(expression.ExecuteScalar(new BsonDocument()).AsBoolean);
            Assert.Equal(2, counter.Reads);
        }

        [Fact]
        public void Invocation_inside_a_captured_index_keeps_single_client_evaluation()
        {
            var counter = new Counter();
            var root = Expression.Parameter(typeof(Row), "row");
            Expression<Func<int, int>> identity = value => value;
            var index = Expression.Invoke(identity,
                Expression.Call(Expression.Constant(counter), nameof(Counter.NextIndex), null));
            var element = Expression.ArrayIndex(Expression.Constant(new[] { 7 }), index);
            var predicate = Expression.Lambda<Func<Row, bool>>(
                Expression.Equal(Expression.Property(root, nameof(Row.Id)), element), root);
            var expression = new BsonMapper().GetExpression(predicate);
            Assert.Equal(1, counter.Reads);
            Assert.True(expression.ExecuteScalar(new BsonDocument { ["_id"] = 7 }).AsBoolean);
            Assert.False(expression.ExecuteScalar(new BsonDocument { ["_id"] = 8 }).AsBoolean);
            Assert.Equal(1, counter.Reads);
        }

        private static void AssertQuery(Expression<Func<Row, bool>> predicate, Row[] rows, params int[] expected)
        {
            Assert.Equal(expected, rows.Where(predicate.Compile()).Select(row => row.Id).OrderBy(id => id).ToArray());
            using var db = new LiteDatabase(":memory:");
            var collection = db.GetCollection<Row>();
            collection.Insert(rows);
            Assert.Equal(expected, collection.Find(predicate).Select(row => row.Id).OrderBy(id => id).ToArray());
            Assert.Equal(expected.Length, collection.Count(predicate));
        }
    }
}
