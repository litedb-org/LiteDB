using System;
using System.Linq;
using FluentAssertions;
using LiteDB.Engine;
using LiteDB.Tests.Mapper;
using Xunit;

namespace LiteDB.Tests.QueryTest
{
    public class IndexExpressionLoading_Tests
    {
        [Fact]
        public void Ordinary_reads_do_not_reparse_persisted_index_definitions()
        {
            using var db = new LiteDatabase(":memory:");
            var rows = db.GetCollection<RangeOptimization_Tests.Row>("rows");
            rows.InsertBulk(Enumerable.Range(1, 10).Select(i => new RangeOptimization_Tests.Row { Id = i, Score = i }));
            rows.EnsureIndex(x => x.Score);
            rows.EnsureIndex("computed", "Score + 1");
            using var scope = new DirectTranslationScope();
            rows.Query().Where(x => x.Id == 3).FirstOrDefault().Score.Should().Be(3);
            rows.Query().Where(x => x.Score >= 5).Count().Should().Be(6);
            rows.Query().Where(x => x.Score == 7).Exists().Should().BeTrue();
            rows.Query().Where(x => x.Score >= 8).ToArray().Select(x => x.Score).Should().Equal(8, 9, 10);
        }

        [Fact]
        public void Persisted_metadata_defers_parsing_and_retains_the_expression_for_reuse()
        {
            var index = new CollectionIndex(1, 0, "computed", "$.Score+1", false);
            var buffer = new BufferSlice(new byte[1024], 0, 1024);
            using (var writer = new BufferWriter(buffer)) index.UpdateBuffer(writer);
            CollectionIndex restored;
            using (var scope = new DirectTranslationScope())
            using (var reader = new BufferReader(buffer))
            {
                restored = new CollectionIndex(reader);
                restored.Name.Should().Be("computed");
                restored.Expression.Should().Be("$.Score+1");
                restored.Slot.Should().Be(1);
            }
            var expression = restored.BsonExpr;
            expression.ExecuteScalar(new BsonDocument { ["Score"] = 5 }).AsInt32.Should().Be(6);
            restored.BsonExpr.Should().BeSameAs(expression);
        }

        [Fact]
        public void Inserts_and_updates_still_evaluate_expression_indexes()
        {
            using var db = new LiteDatabase(":memory:");
            var rows = db.GetCollection("rows");
            rows.EnsureIndex("computed", "Score + 1");
            rows.Insert(new BsonDocument { ["_id"] = 1, ["Score"] = 5 });
            rows.Query().Where("Score + 1 = 6").Count().Should().Be(1);
            rows.Update(new BsonDocument { ["_id"] = 1, ["Score"] = 8 });
            rows.Query().Where("Score + 1 = 6").Count().Should().Be(0);
            rows.Query().Where("Score + 1 = 9").Count().Should().Be(1);
            rows.Delete(1).Should().BeTrue();
            rows.Query().Where("Score + 1 = 9").Count().Should().Be(0);
        }

        [Fact]
        public void New_index_definitions_still_validate_their_expression_eagerly()
        {
            Action create = () => new CollectionIndex(1, 0, "invalid", "$.Score +", false);
            create.Should().Throw<LiteException>();
        }
    }
}
