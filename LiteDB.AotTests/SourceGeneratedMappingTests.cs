using System;
using System.Collections.Generic;
using System.IO;

using LiteDB.Generated;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LiteDB.AotTests
{
    [TestClass]
    public sealed class SourceGeneratedMappingTests
    {
        [TestMethod]
        public void GetGeneratedCollection_WithoutRegistration_Throws()
        {
            var path = GetDatabasePath();

            try
            {
                using var database = new LiteDatabase(path, new BsonMapper());

                var exception = Assert.ThrowsException<InvalidOperationException>(() =>
                    database.GetGeneratedCollection<GeneratedRecord>("records"));

                StringAssert.Contains(exception.Message, typeof(GeneratedRecord).FullName);
            }
            finally
            {
                File.Delete(path);
            }
        }

        [TestMethod]
        public void RegisterGeneratedMappings_DuplicateRegistration_Throws()
        {
            var mapper = new BsonMapper();
            LiteDbGeneratedMappings.Register(mapper);

            Assert.ThrowsException<InvalidOperationException>(() =>
                LiteDbGeneratedMappings.Register(mapper));
        }

        [TestMethod]
        public void GetGeneratedCollection_RoundTripsScalarAttributesAndList()
        {
            var path = GetDatabasePath();

            try
            {
                var mapper = new BsonMapper();
                LiteDbGeneratedMappings.Register(mapper);

                using var database = new LiteDatabase(path, mapper);
                var collection = database.GetGeneratedCollection<GeneratedRecord>("records");
                collection.Insert(new GeneratedRecord
                {
                    Name = "generated",
                    Values = ["one", "two"],
                    Ignored = "not persisted"
                });

                var result = collection.FindById(1);
                var document = database.GetCollection("records").FindById(1);

                Assert.IsNotNull(result);
                Assert.AreEqual("generated", result.Name);
                CollectionAssert.AreEqual(new[] { "one", "two" }, result.Values);
                Assert.IsNull(result.Ignored);
                Assert.AreEqual("generated", document["name"].AsString);
                Assert.IsFalse(document.ContainsKey(nameof(GeneratedRecord.Ignored)));
            }
            finally
            {
                File.Delete(path);
            }
        }

        [TestMethod]
        public void GetGeneratedCollection_RespectsBsonIdAutoIdSetting()
        {
            var path = GetDatabasePath();

            try
            {
                var mapper = new BsonMapper();
                LiteDbGeneratedMappings.Register(mapper);

                using var database = new LiteDatabase(path, mapper);
                var collection = database.GetGeneratedCollection<ExplicitIdRecord>("explicitIds");
                collection.Insert(new ExplicitIdRecord { Id = 42, Name = "manual" });

                var result = collection.FindById(42);

                Assert.IsNotNull(result);
                Assert.AreEqual("manual", result.Name);
            }
            finally
            {
                File.Delete(path);
            }
        }

        private static string GetDatabasePath()
        {
            return Path.Combine(Path.GetTempPath(), $"litedb-source-generated-test-{Guid.NewGuid():N}.db");
        }
    }

    [BsonSourceGenerated]
    public sealed class GeneratedRecord
    {
        public int Id { get; set; }

        [BsonField("name")]
        public string Name { get; set; } = string.Empty;

        public List<string> Values { get; set; } = [];

        [BsonIgnore]
        public string? Ignored { get; set; }
    }

    [BsonSourceGenerated]
    public sealed class ExplicitIdRecord
    {
        [BsonId(false)]
        public int Id { get; set; }

        public string Name { get; set; } = string.Empty;
    }
}
