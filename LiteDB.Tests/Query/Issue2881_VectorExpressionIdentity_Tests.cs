using System;
using System.Linq;
using FluentAssertions;
using LiteDB.Vector;
using Xunit;

namespace LiteDB.Tests.QueryTest
{
    public class Issue2881_VectorExpressionIdentity_Tests
    {
        private const string Upper = "IIF($.Kind='A',$.Embedding,$.Other)";
        private const string Lower = "IIF($.Kind='a',$.Embedding,$.Other)";

        [Theory]
        [InlineData(false, false)]
        [InlineData(false, true)]
        [InlineData(true, false)]
        [InlineData(true, true)]
        public void Different_string_literals_do_not_select_the_same_vector_index(bool threshold, bool scored)
        {
            using var db = new LiteDatabase(new ConnectionString { Filename = ":memory:", Collation = Collation.Binary });
            var docs = Populate(db);
            var query = docs.Query();
            if (threshold) query.WhereNear(BsonExpression.Create(Lower), new[] { 1f, 0f }, 0.1);
            else query.TopKNear(BsonExpression.Create(Lower), new[] { 1f, 0f }, 1);

            var ids = scored ? query.WithScore().Select(x => x.Document["_id"].AsInt32)
                : query.ToArray().Select(x => x["_id"].AsInt32);
            ids.Should().Equal(2);
            query.GetPlan()["index"]["name"].AsString.Should().Be("_id");
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void Different_string_literals_are_rejected_before_mutating_composed_queries(bool topKFirst)
        {
            using var db = new LiteDatabase(new ConnectionString { Filename = ":memory:", Collation = Collation.Binary });
            var docs = Populate(db);
            var query = docs.Query();
            if (topKFirst) query.TopKNear(BsonExpression.Create(Upper), new[] { 1f, 0f }, 1);
            else query.WhereNear(BsonExpression.Create(Upper), new[] { 1f, 0f }, 0.1);

            Action append = () =>
            {
                if (topKFirst) query.WhereNear(BsonExpression.Create(Lower), new[] { 1f, 0f }, 0.1);
                else query.TopKNear(BsonExpression.Create(Lower), new[] { 1f, 0f }, 1);
            };

            append.Should().Throw<InvalidOperationException>();
            query.ToArray().Select(x => x["_id"].AsInt32).Should().Equal(1);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void Ordering_by_a_different_literal_keeps_its_scalar_expression(bool scored)
        {
            using var db = new LiteDatabase(new ConnectionString { Filename = ":memory:", Collation = Collation.Binary });
            var docs = Populate(db);
            var query = docs.Query().WhereNear(BsonExpression.Create(Upper), new[] { 1f, 0f }, 2);
            query.OrderBy(BsonExpression.Create($"VECTOR_SIM({Lower}, [1,0])"));

            var ids = scored ? query.WithScore().Select(x => x.Document["_id"].AsInt32)
                : query.ToArray().Select(x => x["_id"].AsInt32);
            ids.Should().Equal(2, 1);
        }

        [Fact]
        public void Field_name_casing_still_matches_the_vector_index_and_threshold()
        {
            using var db = new LiteDatabase(new ConnectionString { Filename = ":memory:", Collation = Collation.Binary });
            var docs = Populate(db);
            var query = docs.Query().WhereNear(BsonExpression.Create(Upper), new[] { 1f, 0f }, 0.1);
            query.TopKNear(BsonExpression.Create("IIF($.kind='A',$.embedding,$.other)"), new[] { 1f, 0f }, 1);

            query.GetPlan()["index"]["name"].AsString.Should().Be("computed");
            query.WithScore().Single().Document["_id"].AsInt32.Should().Be(1);
        }

        private static ILiteCollection<BsonDocument> Populate(LiteDatabase db)
        {
            var docs = db.GetCollection("docs");
            docs.Insert(new[] { "A", "a" }.Select((kind, index) => new BsonDocument
            {
                ["_id"] = index + 1, ["Kind"] = kind,
                ["Embedding"] = new BsonVector(new[] { 1f, 0f }),
                ["Other"] = new BsonVector(new[] { 0f, 1f })
            }));
            docs.EnsureIndex("computed", BsonExpression.Create(Upper), new VectorIndexOptions(2));
            return docs;
        }
    }
}
