using System;
using System.Linq;
using System.Linq.Expressions;
using FluentAssertions;
using Xunit;

namespace LiteDB.Tests.Mapper
{
    public class LinqCacheStructure_Tests
    {
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void Array_shape_keys_preserve_child_boundaries(bool reverse)
        {
            // Use initializers for both empty and nonempty arrays so the flattened
            // node kinds and types match; only their parent-child edges differ.
            Expression<Func<Row, object[]>> siblings = x => new object[] { new object[] { }, 7 };
            Expression<Func<Row, object[]>> nested = x => new object[] { new object[] { 7 } };
            AssertDistinctShapes(reverse ? nested : siblings, reverse ? siblings : nested);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void Member_initializer_shape_keys_preserve_child_boundaries(bool reverse)
        {
            Expression<Func<Row, Row>> siblings = x => new Row { Next = new Row { }, Value = 7 };
            Expression<Func<Row, Row>> nested = x => new Row { Next = new Row { Value = 7 } };
            AssertDistinctShapes(reverse ? nested : siblings, reverse ? siblings : nested);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void Compiled_capture_evaluators_preserve_array_child_boundaries(bool reverse)
        {
            Expression<Func<Row, int>> siblings = x => Count(new object[] { new object[] { }, 7 });
            Expression<Func<Row, int>> nested = x => Count(new object[] { new object[] { 7 } });
            AssertDistinctShapes(reverse ? nested : siblings, reverse ? siblings : nested);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void Compiled_capture_evaluators_preserve_initializer_child_boundaries(bool reverse)
        {
            Expression<Func<Row, int>> siblings = x => Read(new Row { Next = new Row { }, Value = 7 });
            Expression<Func<Row, int>> nested = x => Read(new Row { Next = new Row { Value = 7 } });
            AssertDistinctShapes(reverse ? nested : siblings, reverse ? siblings : nested);
        }

        [Theory]
        [InlineData("conditional")]
        [InlineData("coalesce")]
        [InlineData("and")]
        [InlineData("or")]
        public void Math_min_and_max_in_runtime_branches_are_not_sequence_accesses(string branch)
        {
            var low = 3;
            var high = 7;
            Expression<Func<Row, int>> query = branch switch
            {
                "conditional" => x => DateTime.Now.Year > 0 ? Math.Min(low, high) : Math.Max(low, high),
                "coalesce" => x => (DateTime.Now.Year > 0 ? (int?)null : DateTime.Now.Year) ?? Math.Max(low, high),
                "and" => x => DateTime.Now.Year > 0 && Math.Min(low, high) > 0 ? Math.Max(low, high) : 0,
                _ => x => DateTime.Now.Year < 0 || Math.Max(low, high) > 0 ? Math.Min(low, high) : 0
            };
            var mapper = new BsonMapper();
            for (var i = 0; i < 3; i++)
            {
                var translated = mapper.GetExpression(query);
                translated.Source.Should().Contain("NOW()");
                translated.ExecuteScalar(new BsonDocument()).AsInt32.Should().Be(query.Compile()(new Row()));
                low += 10;
                high += 20;
            }
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void Runtime_branches_still_reject_closed_sequence_extrema(bool queryable)
        {
            var empty = new int[0];
            var sequence = empty.AsQueryable();
            Expression<Func<Row, int>> min = queryable
                ? x => DateTime.Now.Year > 0 ? 1 : sequence.Min()
                : x => DateTime.Now.Year > 0 ? 1 : empty.Min();
            Expression<Func<Row, int>> max = queryable
                ? x => DateTime.Now.Year > 0 ? 1 : sequence.Max()
                : x => DateTime.Now.Year > 0 ? 1 : empty.Max();
            var mapper = new BsonMapper();
            Action translateMin = () => mapper.GetExpression(min);
            Action translateMax = () => mapper.GetExpression(max);
            translateMin.Should().Throw<NotSupportedException>().WithMessage("*Captured branches*");
            translateMax.Should().Throw<NotSupportedException>().WithMessage("*Captured branches*");
        }

        private static void AssertDistinctShapes<T>(Expression<Func<Row, T>> first, Expression<Func<Row, T>> second)
        {
            var mapper = new BsonMapper();
            foreach (var query in new[] { first, second, first, second })
            {
                var actual = mapper.GetExpression(query);
                BsonExpression fresh;
                using (new DirectTranslationScope()) fresh = new BsonMapper().GetExpression(query);
                actual.Source.Should().Be(fresh.Source);
                actual.ExecuteScalar(new BsonDocument()).Should().Be(fresh.ExecuteScalar(new BsonDocument()));
            }
        }

        private static int Count(object[] values) => values.Length;
        private static int Read(Row value) => value.Value;

        public class Row
        {
            public int Value { get; set; }
            public Row Next { get; set; }
        }
    }
}
