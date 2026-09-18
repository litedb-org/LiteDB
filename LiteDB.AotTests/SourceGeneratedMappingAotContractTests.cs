using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;

using LiteDB.Generated;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LiteDB.AotTests
{
    /// <summary>
    /// A generated collection must never reach runtime model mapping, and ordinary use of the same mapper
    /// must not disable it.
    /// </summary>
    [TestClass]
    public sealed class SourceGeneratedMappingAotContractTests
    {
        [TestMethod]
        public void GeneratedLinq_SerializesCapturedScalarsLikeTheOrdinaryMapper()
        {
            var mapper = new NoRuntimeMappingMapper();
            LiteDbGeneratedMappings.Register(mapper);

            using var database = new LiteDatabase(new MemoryStream(), mapper);
            var collection = database.GetGeneratedCollection<NativeScalarRecord>("captured_scalars");
            var offset = new DateTimeOffset(2024, 3, 4, 5, 6, 7, TimeSpan.FromHours(2));
            collection.Insert(new NativeScalarRecord
            {
                Name = "match",
                Character = 'x',
                UnsignedInteger = uint.MaxValue,
                UnsignedLong = ulong.MaxValue,
                SingleValue = 1.5f,
                SignedShort = -3,
                State = NativeScalarState.Captured,
                TimestampWithOffset = offset
            });
            collection.Insert(new NativeScalarRecord { Name = "other" });

            var character = 'x';
            var unsignedInteger = uint.MaxValue;
            var unsignedLong = ulong.MaxValue;
            var single = 1.5f;
            short signedShort = -3;
            var state = NativeScalarState.Captured;

            Assert.AreEqual(1, collection.Count(x => x.Character == character));
            Assert.AreEqual(1, collection.Count(x => x.UnsignedInteger == unsignedInteger));
            Assert.AreEqual(1, collection.Count(x => x.UnsignedLong == unsignedLong));
            Assert.AreEqual(1, collection.Count(x => x.SingleValue == single));
            Assert.AreEqual(1, collection.Count(x => x.SignedShort == signedShort));
            Assert.AreEqual(1, collection.Count(x => x.State == state));
            Assert.AreEqual(1, collection.Count(x => x.TimestampWithOffset == offset));
        }

        [TestMethod]
        public void GeneratedLinq_SerializesCapturedCollectionsAndGeneratedEntities()
        {
            var mapper = new NoRuntimeMappingMapper();
            LiteDbGeneratedMappings.Register(mapper);

            using var database = new LiteDatabase(new MemoryStream(), mapper);
            var collection = database.GetGeneratedCollection<GeneratedScalarRecord>("captured_collections");
            collection.Insert(new[]
            {
                new GeneratedScalarRecord { Name = "first", Score = 1 },
                new GeneratedScalarRecord { Name = "second", Score = 2 },
                new GeneratedScalarRecord { Name = "third", Score = 3 }
            });

            var scores = new List<int> { 1, 3 };
            var names = new[] { "second" };

            CollectionAssert.AreEquivalent(
                new[] { "first", "third" },
                collection.Find(x => scores.Contains(x.Score)).Select(x => x.Name).ToArray());
            CollectionAssert.AreEquivalent(
                new[] { "second" },
                collection.Find(x => names.Contains(x.Name)).Select(x => x.Name).ToArray());
        }

        [TestMethod]
        public void GeneratedLinq_SerializesCapturedStringKeyedDictionaries()
        {
            var mapper = new NoRuntimeMappingMapper();
            LiteDbGeneratedMappings.Register(mapper);

            using var database = new LiteDatabase(new MemoryStream(), mapper);
            var collection = database.GetGeneratedCollection<DynamicDictionaryRecord>("captured_dictionary");
            collection.Insert(new DynamicDictionaryRecord { Fields = new Dictionary<string, object?> { ["kind"] = "a", ["level"] = 2 } });
            collection.Insert(new DynamicDictionaryRecord { Fields = new Dictionary<string, object?> { ["kind"] = "b" } });

            var wanted = new Dictionary<string, object?> { ["kind"] = "a", ["level"] = 2 };
            var nonStringKeys = new Dictionary<int, string> { [1] = "a" };

            Assert.AreEqual(1, collection.Count(x => x.Fields == wanted));
            Assert.ThrowsException<NotSupportedException>(() => collection.Count(x => x.Fields.Equals(nonStringKeys)));
        }

        [TestMethod]
        public void GeneratedLinq_RejectsACapturedApplicationObject()
        {
            using var database = new LiteDatabase(new MemoryStream(), SourceGeneratedMappingTestHelper.CreateGeneratedMapper());
            var collection = database.GetGeneratedCollection<GeneratedScalarRecord>("captured_object");
            var captured = new object[] { new UnmappedValue() };

            var exception = Assert.ThrowsException<NotSupportedException>(
                () => collection.Find(x => captured.Contains(x.Name)).ToArray());

            StringAssert.Contains(exception.Message, typeof(UnmappedValue).FullName);
        }

        [TestMethod]
        public void OrdinaryGroupBy_DoesNotDisableGeneratedCollectionsOnTheSameMapper()
        {
            using var database = new LiteDatabase(new MemoryStream(), SourceGeneratedMappingTestHelper.CreateGeneratedMapper());

            var ordinary = database.GetCollection<UnmappedValue>("ordinary_grouping");
            ordinary.Insert(new UnmappedValue { Id = 1, Category = "a" });
            Assert.AreEqual(1, ordinary.Query().GroupBy(x => x.Category).ToArray().Length);

            var generated = database.GetGeneratedCollection<GeneratedScalarRecord>("generated_after_grouping");
            generated.Insert(new GeneratedScalarRecord { Name = "still-works", Score = 1 });

            Assert.AreEqual("still-works", generated.FindById(1).Name);
        }

        private sealed class UnmappedValue
        {
            public int Id { get; set; }

            public string Category { get; set; } = string.Empty;
        }

        /// <summary>
        /// Fails the test as soon as anything reaches reflection-based object mapping.
        /// </summary>
        private sealed class NoRuntimeMappingMapper : BsonMapper
        {
            protected override IEnumerable<MemberInfo> GetTypeMembers(Type type) =>
                throw new AssertFailedException($"Generated execution discovered members of '{type}' at runtime.");

            protected override BsonDocument SerializeObject(Type type, object obj, int depth) =>
                throw new AssertFailedException($"Generated execution serialized '{type}' through runtime mapping.");

            protected override void DeserializeObject(Type type, object obj, BsonDocument value) =>
                throw new AssertFailedException($"Generated execution deserialized '{type}' through runtime mapping.");
        }
    }
}
