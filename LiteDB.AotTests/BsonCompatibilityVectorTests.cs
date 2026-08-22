using LiteDB.Generated;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LiteDB.AotTests
{
    [TestClass]
    public sealed class BsonCompatibilityVectorTests
    {
        [TestMethod]
        public void ScalarRecord_MatchesGoldenAndCrossReadsWithoutGenericConversion()
        {
            var golden = new BsonDocument
            {
                ["_id"] = 1,
                [nameof(PhaseCScalarRecord.Score)] = 42
            };
            var vector = new BsonCompatibilityVector<PhaseCScalarRecord, PhaseCScalarRecord>
            {
                CreateOrdinary = CreateEntity,
                CreateGenerated = CreateEntity,
                CreateOrdinaryMapper = CreateOrdinaryMapper,
                CreateGeneratedMapper = CreateGuardedGeneratedMapper,
                Golden = golden,
                VerifyOrdinary = VerifyEntity,
                VerifyGenerated = VerifyEntity
            };

            vector.Execute();

            Assert.AreEqual(BsonCompatibilityAssert.Fingerprint(golden),
                BsonCompatibilityAssert.Fingerprint(new BsonDocument
                {
                    [nameof(PhaseCScalarRecord.Score)] = 42,
                    ["_id"] = 1
                }));
        }

        [TestMethod]
        public void TypedComparison_RejectsTypeFieldNullArrayAndNestedShapeChanges()
        {
            var expected = new BsonDocument
            {
                ["number"] = 1,
                ["nullable"] = BsonValue.Null,
                ["items"] = new BsonArray { "a", "b" },
                ["nested"] = new BsonDocument { ["payload"] = new byte[] { 0, 127, 255 } }
            };

            Assert.ThrowsException<AssertFailedException>(() => BsonCompatibilityAssert.AreEqual(expected,
                new BsonDocument { ["number"] = 1L, ["nullable"] = BsonValue.Null, ["items"] = new BsonArray { "a", "b" }, ["nested"] = new BsonDocument { ["payload"] = new byte[] { 0, 127, 255 } } }));
            Assert.ThrowsException<AssertFailedException>(() => BsonCompatibilityAssert.AreEqual(expected,
                new BsonDocument { ["renamed"] = 1, ["nullable"] = BsonValue.Null, ["items"] = new BsonArray { "a", "b" }, ["nested"] = new BsonDocument { ["payload"] = new byte[] { 0, 127, 255 } } }));
            Assert.ThrowsException<AssertFailedException>(() => BsonCompatibilityAssert.AreEqual(expected,
                new BsonDocument { ["number"] = 1, ["items"] = new BsonArray { "a", "b" }, ["nested"] = new BsonDocument { ["payload"] = new byte[] { 0, 127, 255 } } }));
            Assert.ThrowsException<AssertFailedException>(() => BsonCompatibilityAssert.AreEqual(expected,
                new BsonDocument { ["number"] = 1, ["nullable"] = BsonValue.Null, ["items"] = new BsonArray { "b", "a" }, ["nested"] = new BsonDocument { ["payload"] = new byte[] { 0, 127, 255 } } }));
            Assert.ThrowsException<AssertFailedException>(() => BsonCompatibilityAssert.AreEqual(expected,
                new BsonDocument { ["number"] = 1, ["nullable"] = BsonValue.Null, ["items"] = new BsonArray { "a", "b" }, ["nested"] = new BsonDocument { ["different"] = new byte[] { 0, 127, 255 } } }));
        }

        private static PhaseCScalarRecord CreateEntity() => new()
        {
            Score = 42
        };

        private static BsonMapper CreateOrdinaryMapper() => new();

        private static BsonMapper CreateGuardedGeneratedMapper()
        {
            var mapper = new ThrowingConversionMapper();
            LiteDbGeneratedMappings.Register(mapper);
            return mapper;
        }

        private static void VerifyEntity(PhaseCScalarRecord entity, BsonValue id)
        {
            Assert.AreEqual(1, id.AsInt32);
            Assert.AreEqual(1, entity.Id);
            Assert.AreEqual(42, entity.Score);
            Assert.IsNull(entity.Name);
        }
    }
}
