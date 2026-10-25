using System;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using LiteDB.Tests.Mapper;
using Xunit;

namespace LiteDB.Tests.QueryTest
{
    public class QueryAggregateExpressions_Tests
    {
        [Fact]
        public void Aggregate_helpers_do_not_tokenize_with_an_existing_snapshot()
        {
            using var db = new LiteDatabase(":memory:");
            var rows = db.GetCollection<RangeOptimization_Tests.Row>("rows");
            rows.InsertBulk(Enumerable.Range(1, 5).Select(i => new RangeOptimization_Tests.Row { Id = i, Score = i }));
            rows.EnsureIndex(x => x.Score);
            db.BeginTrans();
            rows.Query().FirstOrDefault(); // Load persisted index definitions before the guard.
            using var scope = new DirectTranslationScope();
            rows.Query().Where(x => x.Score >= 3).Count().Should().Be(3);
            rows.Query().Where(x => x.Score >= 4).LongCount().Should().Be(2);
            rows.Query().Where(x => x.Score >= 5).Exists().Should().BeTrue();
            rows.Query().Where(x => x.Score > 5).Exists().Should().BeFalse();
        }

        [Fact]
        public void Shared_templates_match_the_previous_canonical_aggregate_expressions()
        {
            ExpressionParity.AssertMetadata(QueryAggregateExpressions.Count, BsonExpression.Create("{ count: COUNT(*._id) }"));
            ExpressionParity.AssertMetadata(QueryAggregateExpressions.Exists, BsonExpression.Create("{ exists: ANY(*._id) }"));
        }

        [Fact]
        public void Failed_helpers_restore_the_callers_projection()
        {
            using var db = new LiteDatabase(":memory:");
            var rows = db.GetCollection("rows");
            rows.Insert(new BsonDocument { ["Name"] = "row" });
            var query = rows.Query().Where("SUBSTRING(Name, 1000) = 'x'").Select("{ label: Name }");
            var select = query.GetPlan()["select"]["expr"].AsString;
            Action count = () => query.Count();
            Action exists = () => query.Exists();
            count.Should().Throw<ArgumentOutOfRangeException>();
            exists.Should().Throw<ArgumentOutOfRangeException>();
            query.GetPlan()["select"]["expr"].AsString.Should().Be(select);
        }

        [Fact]
        public async Task Shared_templates_do_not_share_collections_or_current_values()
        {
            await Task.WhenAll(Enumerable.Range(1, 16).Select(worker => Task.Run(() =>
            {
                using var db = new LiteDatabase(":memory:", new BsonMapper());
                var rows = db.GetCollection<RangeOptimization_Tests.Row>("rows" + worker);
                rows.InsertBulk(Enumerable.Range(1, worker).Select(i => new RangeOptimization_Tests.Row { Id = i, Score = i }));
                rows.Query().GroupBy(x => x.Id).Limit(1).Count().Should().Be(1);
                rows.Query().GroupBy(x => x.Id).Limit(1).Exists().Should().BeTrue();
                for (var i = 0; i < 10; i++)
                {
                    rows.Query().Where(x => x.Score > i).Count().Should().Be(Math.Max(0, worker - i));
                    rows.Query().Where(x => x.Score > i).Exists().Should().Be(worker > i);
                }
            })));
            QueryAggregateExpressions.Count.Parameters.Count.Should().Be(0);
            QueryAggregateExpressions.Exists.Parameters.Count.Should().Be(0);
        }
    }
}
