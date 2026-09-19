using System;
using System.Linq;
using FluentAssertions;
using LiteDB.Vector;
using Xunit;

namespace LiteDB.Tests
{
    public class VectorSearchResult_Tests
    {
        public class Item
        {
            public int Id { get; set; }
            [BsonField("vector")]
            public float[] Embedding { get; set; }
            public string Score { get; set; }
        }

        private static ILiteCollection<Item> Populate(LiteDatabase db, VectorDistanceMetric? metric)
        {
            var collection = db.GetCollection<Item>("items");
            collection.Insert(new[]
            {
                new Item { Id = 1, Embedding = new[] { 1.75f, 0f }, Score = "stored" },
                new Item { Id = 2, Embedding = new[] { 1f, 1f }, Score = "stored" },
                new Item { Id = 3, Embedding = new[] { -1f, 0f }, Score = "stored" }
            });
            if (metric.HasValue)
            {
                collection.EnsureIndex("vectors", x => x.Embedding, new VectorIndexOptions(2, metric.Value));
            }
            return collection;
        }

        [Theory]
        [InlineData(null)]
        [InlineData(VectorDistanceMetric.Cosine)]
        [InlineData(VectorDistanceMetric.Euclidean)]
        [InlineData(VectorDistanceMetric.DotProduct)]
        public void TopK_ReturnsMetricScoresAndAllowsProjection(VectorDistanceMetric? metric)
        {
            using var db = new LiteDatabase(":memory:");
            var collection = Populate(db, metric);
            var results = collection.Query().TopKNearWithScore(x => x.Embedding, new[] { 1f, 0f }, 2)
                .Select(hit => new { hit.Document.Id, hit.Document.Score, Value = hit.Score, hit.Metric }).ToArray();

            results.Select(x => x.Id).Should().Equal(1, 2);
            results.Should().OnlyContain(x => x.Score == "stored");
            results.Should().OnlyContain(x => x.Metric == (metric ?? VectorDistanceMetric.Cosine));
            var expected = metric == VectorDistanceMetric.DotProduct ? new[] { 1.75d, 1d }
                : metric == VectorDistanceMetric.Euclidean ? new[] { 0.75d, 1d }
                : new[] { 0d, 1d - 1d / Math.Sqrt(2d) };
            for (var i = 0; i < results.Length; i++)
            {
                results[i].Value.Should().HaveValue().And.BeApproximately(expected[i], 1e-6);
            }
        }

        [Theory]
        [InlineData(null, 0.3)]
        [InlineData(VectorDistanceMetric.Cosine, 0.3)]
        [InlineData(VectorDistanceMetric.Euclidean, 1.1)]
        [InlineData(VectorDistanceMetric.DotProduct, 0.9)]
        public void ThresholdOverloads_ReturnSameDocumentsAndScores(VectorDistanceMetric? metric, double threshold)
        {
            using var db = new LiteDatabase(":memory:");
            var collection = Populate(db, metric);
            var target = new[] { 1f, 0f };
            var expected = collection.Query().WhereNear(x => x.Embedding, target, threshold).ToArray();
            var results = new[]
            {
                collection.Query().WhereNearWithScore(x => x.Embedding, target, threshold).ToArray(),
                collection.Query().WhereNearWithScore("vector", target, threshold).ToArray(),
                collection.Query().WhereNearWithScore(BsonExpression.Create("$.vector"), target, threshold).ToArray(),
                collection.Query().FindNearestWithScore(x => x.Embedding, target, threshold).ToArray(),
                collection.Query().FindNearestWithScore("vector", target, threshold).ToArray(),
                collection.Query().FindNearestWithScore(BsonExpression.Create("$.vector"), target, threshold).ToArray()
            };
            foreach (var result in results)
            {
                result.Select(x => x.Document.Id).Should().Equal(expected.Select(x => x.Id));
                result.Should().HaveCount(2);
                result.Select(x => x.Score).Should().Equal(results[0].Select(x => x.Score));
            }
        }

        [Fact]
        public void UntypedDocuments_KeepStoredScoreAndSupportFieldOverloads()
        {
            using var db = new LiteDatabase(":memory:");
            Populate(db, VectorDistanceMetric.DotProduct);
            var collection = db.GetCollection("items");
            var target = new[] { 1f, 0f };
            var stringHit = collection.Query().TopKNearWithScore("vector", target, 1).Single();
            var expressionHit = collection.Query().TopKNearWithScore(BsonExpression.Create("$.vector"), target, 1).Single();

            stringHit.Score.Should().Be(1.75);
            expressionHit.Score.Should().Be(stringHit.Score);
            stringHit.Document["Score"].AsString.Should().Be("stored");
            collection.FindById(1).Keys.Should().BeEquivalentTo("_id", "vector", "Score");
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void WithScore_PreservesProjectionAndDoesNotChangeOriginalQuery(bool indexed)
        {
            using var db = new LiteDatabase(":memory:");
            var collection = Populate(db, indexed ? VectorDistanceMetric.DotProduct : (VectorDistanceMetric?)null);
            var query = collection.Query().WhereNear(x => x.Embedding, new[] { 1f, 0f }, double.MaxValue)
                .Select(x => x.Id);
            var scored = query.WithScore().ToArray();

            scored.Select(x => x.Document).Should().Equal(query.ToArray());
            scored[0].Score.Should().Be(indexed ? 1.75 : 0);
            query.ToDocuments().First().Keys.Should().NotContain("Document");
        }

        [Fact]
        public void WithScore_SnapshotsPagingAndCanBeEnumeratedAgain()
        {
            using var db = new LiteDatabase(":memory:");
            var collection = Populate(db, VectorDistanceMetric.DotProduct);
            var target = new[] { 1f, 0f };
            var query = collection.Query().TopKNear(x => x.Embedding, target, 2);
            var results = query.WithScore();
            query.Limit(1);
            target[0] = -1;

            results.Select(x => x.Score).Should().Equal(1.75d, 1d);
            results.Select(x => x.Score).Should().Equal(1.75d, 1d);
        }

        [Fact]
        public void TopK_AppliesFiltersAndOffsetBeforeLimit()
        {
            using var db = new LiteDatabase(":memory:");
            var collection = Populate(db, VectorDistanceMetric.DotProduct);
            var filtered = collection.Query().Where(x => x.Id != 1)
                .TopKNearWithScore(x => x.Embedding, new[] { 1f, 0f }, 2).ToArray();
            var paged = collection.Query().TopKNear(x => x.Embedding, new[] { 1f, 0f }, 2)
                .Skip(1).WithScore().ToArray();

            filtered.Select(x => x.Document.Id).Should().Equal(2, 3);
            filtered.Select(x => x.Score).Should().Equal(1d, -1d);
            paged.Select(x => x.Document.Id).Should().Equal(2, 3);
            paged.Select(x => x.Score).Should().Equal(1d, -1d);
        }

        [Fact]
        public void WithScore_PreservesScoresAcrossExplicitSort()
        {
            using var db = new LiteDatabase(":memory:");
            var collection = Populate(db, VectorDistanceMetric.DotProduct);
            var results = collection.Query().WhereNear(x => x.Embedding, new[] { 1f, 0f }, double.MaxValue)
                .OrderByDescending(x => x.Id).Limit(2).WithScore().ToArray();

            results.Select(x => x.Document.Id).Should().Equal(3, 2);
            results.Select(x => x.Score).Should().Equal(-1d, 1d);
        }

        [Fact]
        public void EmptyCollection_ReturnsNoHits()
        {
            using var db = new LiteDatabase(":memory:");
            db.GetCollection<Item>().Query().TopKNearWithScore(x => x.Embedding, new[] { 1f, 0f }, 2)
                .Should().BeEmpty();
        }

        [Fact]
        public void UnindexedUndefinedCosine_ReturnsNullScore()
        {
            using var db = new LiteDatabase(":memory:");
            var collection = db.GetCollection<Item>();
            collection.Insert(new Item { Id = 1, Embedding = new[] { 0f, 0f } });
            var result = collection.Query().TopKNearWithScore(x => x.Embedding, new[] { 1f, 0f }, 1).Single();

            result.Score.Should().BeNull();
            result.Metric.Should().Be(VectorDistanceMetric.Cosine);
        }

        [Fact]
        public void IncompatibleIndex_ReportsFallbackMetricAndNullScore()
        {
            using var db = new LiteDatabase(":memory:");
            var collection = Populate(db, VectorDistanceMetric.Euclidean);
            var result = collection.Query().TopKNearWithScore(x => x.Embedding, new[] { 1f, 0f, 0f }, 1).Single();

            result.Metric.Should().Be(VectorDistanceMetric.Cosine);
            result.Score.Should().BeNull();
        }

        [Fact]
        public void Projection_UsesCollectionMapperAndPreservesDocumentScore()
        {
            var mapper = new BsonMapper();
            mapper.Entity<Item>().Field(x => x.Score, "label");
            using var db = new LiteDatabase(":memory:", mapper);
            var collection = Populate(db, VectorDistanceMetric.DotProduct);
            var result = collection.Query().WhereNear(x => x.Embedding, new[] { 1f, 0f }, 1.5)
                .Select(x => new { x.Id, x.Score }).WithScore().Single();

            result.Document.Id.Should().Be(1);
            result.Document.Score.Should().Be("stored");
            result.Score.Should().Be(1.75);
        }

        [Fact]
        public void BsonScalarProjection_PreservesOrdinaryDocumentShape()
        {
            using var db = new LiteDatabase(":memory:");
            var collection = Populate(db, VectorDistanceMetric.DotProduct);
            var query = collection.Query().WhereNear(x => x.Embedding, new[] { 1f, 0f }, 1.5)
                .Select(BsonExpression.Create("$._id"));
            var result = query.WithScore().Single();

            Assert.Equal(query.Single(), result.Document);
            result.Score.Should().Be(1.75);
        }

        [Theory]
        [InlineData(VectorDistanceMetric.Cosine, new[] { 4, 3, 1 }, new[] { 0d, 0d, 0d })]
        [InlineData(VectorDistanceMetric.Euclidean, new[] { 4, 3, 2 }, new[] { 0d, 1d, 1d })]
        [InlineData(VectorDistanceMetric.DotProduct, new[] { 1, 3, 4 }, new[] { 3d, 2d, 1d })]
        public void TopK_PreservesMetricAndSecondarySortKeys(VectorDistanceMetric metric, int[] ids, double[] scores)
        {
            using var db = new LiteDatabase(":memory:");
            var collection = db.GetCollection<Item>();
            collection.Insert(new[]
            {
                new Item { Id = 1, Embedding = new[] { 3f, 0f } },
                new Item { Id = 2, Embedding = new[] { 1f, 1f } },
                new Item { Id = 3, Embedding = new[] { 2f, 0f } },
                new Item { Id = 4, Embedding = new[] { 1f, 0f } }
            });
            collection.EnsureIndex(x => x.Embedding, new VectorIndexOptions(2, metric));
            var query = collection.Query();
            query.TopKNear(x => x.Embedding, new[] { 1f, 0f }, 3);
            query.ThenByDescending(x => x.Id);

            query.GetPlan()["orderBy"].IsArray.Should().BeTrue();
            var results = query.WithScore().ToArray();
            results.Select(x => x.Document.Id).Should().Equal(ids);
            results.Select(x => x.Score.Value).Should().Equal(scores);
        }

        [Fact]
        public void WithScore_ValidatesQueryAndArguments()
        {
            using var db = new LiteDatabase(":memory:");
            var collection = Populate(db, null);
            Action missingSearch = () => collection.Query().WithScore();
            Action nullSource = () => ((ILiteQueryable<Item>)null).WithScore();
            Action nullTarget = () => collection.Query().TopKNearWithScore(x => x.Embedding, null, 1);
            Action invalidK = () => collection.Query().TopKNearWithScore(x => x.Embedding, new[] { 1f, 0f }, 0);
            Action grouped = () => collection.Query().WhereNear(x => x.Embedding, new[] { 1f, 0f }, 1)
                .GroupBy(x => x.Id).WithScore();

            missingSearch.Should().Throw<InvalidOperationException>();
            nullSource.Should().Throw<ArgumentNullException>();
            nullTarget.Should().Throw<ArgumentException>();
            invalidK.Should().Throw<ArgumentOutOfRangeException>();
            grouped.Should().Throw<InvalidOperationException>();
        }
    }
}
