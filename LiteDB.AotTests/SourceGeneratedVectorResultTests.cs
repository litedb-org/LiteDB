using System;
using System.Linq;

using LiteDB.Generated;
using LiteDB.Vector;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using static LiteDB.AotTests.SourceGeneratedMappingTestHelper;

namespace LiteDB.AotTests
{
    [TestClass]
    public sealed class SourceGeneratedVectorResultTests
    {
        [TestMethod]
        public void GetGeneratedCollection_VectorResults_UseGeneratedDeserializer()
        {
            var mapper = new ThrowingRuntimeDeserializationMapper();
            LiteDbGeneratedMappings.Register(mapper);

            using var database = new LiteDatabase(":memory:", mapper);
            var rawCollection = database.GetCollection("generatedVectorResults");
            rawCollection.Insert(new[]
            {
                new BsonDocument
                {
                    ["_id"] = 1,
                    [nameof(GeneratedScalarRecord.Name)] = "nearest",
                    [nameof(GeneratedScalarRecord.Score)] = 10,
                    ["Embedding"] = new BsonVector(new[] { 1f, 0f })
                },
                new BsonDocument
                {
                    ["_id"] = 2,
                    [nameof(GeneratedScalarRecord.Name)] = "farther",
                    [nameof(GeneratedScalarRecord.Score)] = 20,
                    ["Embedding"] = new BsonVector(new[] { 0f, 1f })
                }
            });
            rawCollection.EnsureIndex(
                "embedding",
                BsonExpression.Create("$.Embedding"),
                new VectorIndexOptions(2, VectorDistanceMetric.Euclidean));

            var collection = database.GetGeneratedCollection<GeneratedScalarRecord>("generatedVectorResults");
            var result = collection.Query()
                .TopKNearWithScore(BsonExpression.Create("$.Embedding"), new[] { 1f, 0f }, 1)
                .Single();

            Assert.AreEqual(1, result.Document.Id);
            Assert.AreEqual("nearest", result.Document.Name);
            Assert.AreEqual(10, result.Document.Score);
            Assert.AreEqual(0d, result.Score);
            Assert.AreEqual(VectorDistanceMetric.Euclidean, result.Metric);
        }

        private sealed class ThrowingRuntimeDeserializationMapper : BsonMapper
        {
            public override object Deserialize(Type type, BsonValue value) =>
                throw new AssertFailedException("Generated vector results must not use runtime mapper deserialization.");
        }
    }
}
