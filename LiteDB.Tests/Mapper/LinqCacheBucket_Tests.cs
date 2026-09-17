using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Threading.Tasks;
using FluentAssertions;
using Xunit;

namespace LiteDB.Tests.Mapper
{
    public class LinqCacheBucket_Tests
    {
        [Fact]
        public void Colliding_templates_remain_available_and_do_not_confuse_their_shapes()
        {
            var cases = FindCollisions(2);
            var mapper = new BsonMapper();
            for (var repetition = 0; repetition < 8; repetition++)
            {
                foreach (var item in cases)
                {
                    var expression = mapper.GetExpression(item.Expression);
                    var document = new BsonDocument { ["_id"] = repetition, ["Score"] = repetition + 1 };
                    expression.ExecuteScalar(document).AsArray.Select(x => x.AsInt32).Should().Equal(
                        Enumerable.Repeat(repetition + (item.Member == nameof(Row.Score) ? 1 : 0), item.Length));
                }
                mapper.LinqExpressionCacheCount.Should().Be(2);
            }
        }

        [Fact]
        public void Full_buckets_evict_old_entries_without_growing_or_changing_results()
        {
            var cases = FindCollisions(5);
            var mapper = new BsonMapper();
            for (var repetition = 0; repetition < 3; repetition++)
                foreach (var item in cases)
                {
                    mapper.GetExpression(item.Expression).ExecuteScalar(new BsonDocument { ["_id"] = 7, ["Score"] = 7 })
                        .AsArray.Count.Should().Be(item.Length);
                    mapper.LinqExpressionCacheCount.Should().BeLessThanOrEqualTo(4);
                }
            mapper.LinqExpressionCacheCount.Should().Be(4);
        }

        [Fact]
        public void Concurrent_publication_does_not_fill_a_bucket_with_duplicate_shapes()
        {
            var mapper = new BsonMapper();
            mapper.Entity<Row>();
            Parallel.For(0, 128, value =>
            {
                var expression = mapper.GetExpression<Row, bool>(x => x.Score == value);
                expression.ExecuteScalar(new BsonDocument { ["Score"] = value }).AsBoolean.Should().BeTrue();
            });
            mapper.LinqExpressionCacheCount.Should().Be(1);
        }

        private static List<ShapeCase> FindCollisions(int count)
        {
            var groups = new Dictionary<int, List<ShapeCase>>();
            foreach (var member in new[] { nameof(Row.Id), nameof(Row.Score) })
            {
                var root = Expression.Parameter(typeof(Row), "x");
                var field = Expression.Property(root, member);
                for (var length = 1; length <= 250; length++)
                {
                    var expression = Expression.Lambda<Func<Row, int[]>>(
                        Expression.NewArrayInit(typeof(int), Enumerable.Repeat(field, length)), root);
                    var shape = LinqQueryShape.Rent();
                    try
                    {
                        shape.Visit(expression);
                        shape.Supported.Should().BeTrue();
                        var bucket = LinqExpressionCache.GetBucket(shape.Hash);
                        if (!groups.TryGetValue(bucket, out var group)) groups[bucket] = group = new List<ShapeCase>();
                        group.Add(new ShapeCase { Expression = expression, Member = member, Length = length });
                        if (group.Count == count) return group;
                    }
                    finally { shape.Release(); }
                }
            }
            throw new InvalidOperationException("Expected bounded structural hashes to collide.");
        }

        private sealed class ShapeCase
        {
            internal Expression<Func<Row, int[]>> Expression;
            internal string Member;
            internal int Length;
        }

        public class Row
        {
            public int Id { get; set; }
            public int Score { get; set; }
        }
    }
}
