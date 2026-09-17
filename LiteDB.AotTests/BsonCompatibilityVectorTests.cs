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
        public void SingleLevelInheritedRecord_MatchesGoldenAndCrossReadsWithoutGenericConversion()
        {
            var vector = new BsonCompatibilityVector<SingleLevelExecutionRecord, SingleLevelExecutionRecord>
            {
                CreateOrdinary = static () => new SingleLevelExecutionRecord { Id = 17, BaseValue = 3, DerivedValue = "derived" },
                CreateGenerated = static () => new SingleLevelExecutionRecord { Id = 17, BaseValue = 3, DerivedValue = "derived" },
                CreateOrdinaryMapper = CreateOrdinaryMapper,
                CreateGeneratedMapper = CreateGuardedGeneratedMapper,
                Golden = new BsonDocument { ["_id"] = 17, ["BaseValue"] = 3, ["DerivedValue"] = "derived" },
                VerifyOrdinary = VerifySingleLevel,
                VerifyGenerated = VerifySingleLevel
            };

            vector.Execute();
        }

        [TestMethod]
        public void MultiLevelOverrideRecord_PreservesResolvedAttributesIgnoresAndProjection()
        {
            var vector = new BsonCompatibilityVector<HierarchyExecutionRecord, HierarchyExecutionRecord>
            {
                CreateOrdinary = CreateHierarchy,
                CreateGenerated = CreateHierarchy,
                CreateOrdinaryMapper = CreateOrdinaryMapper,
                CreateGeneratedMapper = CreateGuardedGeneratedMapper,
                Golden = new BsonDocument { ["_id"] = 23, ["middle_name"] = "resolved", ["DerivedScore"] = 9 },
                VerifyOrdinary = VerifyHierarchy,
                VerifyGenerated = VerifyHierarchy
            };

            vector.Execute();
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

        private static HierarchyExecutionRecord CreateHierarchy() => new()
        {
            EntityKey = 23,
            DisplayName = "resolved",
            DerivedScore = 9
        };

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

        private static void VerifySingleLevel(SingleLevelExecutionRecord entity, BsonValue id)
        {
            Assert.AreEqual(17, id.AsInt32);
            Assert.AreEqual(17, entity.Id);
            Assert.AreEqual(3, entity.BaseValue);
            Assert.AreEqual("derived", entity.DerivedValue);
        }

        private static void VerifyHierarchy(HierarchyExecutionRecord entity, BsonValue id)
        {
            Assert.AreEqual(23, id.AsInt32);
            Assert.AreEqual(23, entity.EntityKey);
            Assert.AreEqual("resolved", entity.DisplayName);
            Assert.AreEqual(9, entity.DerivedScore);
            Assert.AreEqual("resolved:9", entity.Projection);
            Assert.IsNull(entity.IgnoredValue);
            Assert.AreEqual(1, entity.IdSetterCalls);
        }
    }

    public class SingleLevelExecutionBase
    {
        public int Id { get; set; }
        public int BaseValue { get; set; }
    }

    [BsonSourceGenerated]
    public sealed class SingleLevelExecutionRecord : SingleLevelExecutionBase
    {
        public string DerivedValue { get; set; } = string.Empty;
    }

    public abstract class HierarchyExecutionBase
    {
        [BsonId(false)]
        public virtual int EntityKey { get; set; }

        [BsonField("base_name")]
        public virtual string DisplayName { get; set; } = string.Empty;

        [BsonIgnore]
        public virtual string? IgnoredValue { get; set; }
    }

    public abstract class HierarchyExecutionMiddle : HierarchyExecutionBase
    {
        public override int EntityKey { get; set; }

        [BsonField("middle_name")]
        public override string DisplayName { get; set; } = string.Empty;

        public override string? IgnoredValue { get; set; }
    }

    [BsonSourceGenerated]
    public sealed class HierarchyExecutionRecord : HierarchyExecutionMiddle
    {
        private int _entityKey;

        public override int EntityKey
        {
            get => _entityKey;
            set
            {
                _entityKey = value;
                IdSetterCalls++;
            }
        }

        public override string DisplayName { get; set; } = string.Empty;
        public override string? IgnoredValue { get; set; }
        public int DerivedScore { get; set; }

        [BsonIgnore]
        public string Projection => $"{DisplayName}:{DerivedScore}";

        [BsonIgnore]
        public int IdSetterCalls { get; private set; }
    }
}
