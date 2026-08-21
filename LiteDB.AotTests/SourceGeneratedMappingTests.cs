using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

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
        public void RegisterGeneratedMappings_NullMapper_Throws()
        {
            var exception = Assert.ThrowsException<ArgumentNullException>(() =>
                LiteDbGeneratedMappings.Register(null!));

            Assert.AreEqual("mapper", exception.ParamName);
        }

        [TestMethod]
        public void GetGeneratedCollection_InvalidName_Throws()
        {
            var path = GetDatabasePath();

            try
            {
                var mapper = new BsonMapper();
                LiteDbGeneratedMappings.Register(mapper);

                using var database = new LiteDatabase(path, mapper);

                var nullException = Assert.ThrowsException<ArgumentNullException>(() =>
                    database.GetGeneratedCollection<GeneratedRecord>(null));
                var emptyException = Assert.ThrowsException<ArgumentNullException>(() =>
                    database.GetGeneratedCollection<GeneratedRecord>(string.Empty));
                var whitespaceException = Assert.ThrowsException<ArgumentNullException>(() =>
                    database.GetGeneratedCollection<GeneratedRecord>("   "));

                Assert.AreEqual("name", nullException.ParamName);
                Assert.AreEqual("name", emptyException.ParamName);
                Assert.AreEqual("name", whitespaceException.ParamName);
            }
            finally
            {
                File.Delete(path);
            }
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

        [TestMethod]
        public void GetGeneratedCollection_RoundTripsNullAndEmptyStringLists()
        {
            var path = GetDatabasePath();

            try
            {
                var mapper = new BsonMapper { SerializeNullValues = true };
                LiteDbGeneratedMappings.Register(mapper);

                using var database = new LiteDatabase(path, mapper);
                var collection = database.GetGeneratedCollection<NullableListRecord>("nullableLists");
                collection.Insert(new NullableListRecord { Id = 1, Values = null });
                collection.Insert(new NullableListRecord { Id = 2, Values = [] });

                var nullValues = collection.FindById(1);
                var emptyValues = collection.FindById(2);
                var nullDocument = database.GetCollection("nullableLists").FindById(1);
                var emptyDocument = database.GetCollection("nullableLists").FindById(2);

                Assert.IsNotNull(nullValues);
                Assert.IsNull(nullValues.Values);
                Assert.IsTrue(nullDocument.ContainsKey(nameof(NullableListRecord.Values)));
                Assert.IsTrue(nullDocument[nameof(NullableListRecord.Values)].IsNull);
                Assert.IsNotNull(emptyValues);
                Assert.IsNotNull(emptyValues.Values);
                Assert.AreEqual(0, emptyValues.Values.Count);
                Assert.AreEqual(0, emptyDocument[nameof(NullableListRecord.Values)].AsArray.Count);
            }
            finally
            {
                File.Delete(path);
            }
        }

        [TestMethod]
        public void GetGeneratedCollection_UsesIdConventionsNamedFieldsAndIgnoredProperties()
        {
            var path = GetDatabasePath();

            try
            {
                var mapper = new BsonMapper();
                LiteDbGeneratedMappings.Register(mapper);

                using var database = new LiteDatabase(path, mapper);
                var collection = database.GetGeneratedCollection<ConventionIdRecord>("conventionIds");
                collection.Insert(new ConventionIdRecord
                {
                    ConventionIdRecordId = 21,
                    Name = "named-field",
                    IgnoredText = "not persisted",
                    IgnoredNumber = 7
                });

                var result = collection.FindById(21);
                var document = database.GetCollection("conventionIds").FindById(21);

                Assert.IsNotNull(result);
                Assert.AreEqual(21, result.ConventionIdRecordId);
                Assert.AreEqual("named-field", result.Name);
                Assert.IsNull(result.IgnoredText);
                Assert.AreEqual(0, result.IgnoredNumber);
                Assert.AreEqual(21, document["_id"].AsInt32);
                Assert.AreEqual("named-field", document["stored_name"].AsString);
                Assert.IsFalse(document.ContainsKey(nameof(ConventionIdRecord.IgnoredText)));
                Assert.IsFalse(document.ContainsKey(nameof(ConventionIdRecord.IgnoredNumber)));
            }
            finally
            {
                File.Delete(path);
            }
        }

        [TestMethod]
        public void GetGeneratedCollection_RoundTripsSupportedScalarCategories()
        {
            var path = GetDatabasePath();
            var expectedTimestamp = new DateTime(2024, 6, 7, 8, 9, 10, DateTimeKind.Local);
            var expectedGuid = new Guid("64c61e5f-18a9-421a-8862-c71c15ea5431");
            var expectedPayload = new byte[] { 1, 2, 3, 255 };

            try
            {
                var mapper = new BsonMapper();
                LiteDbGeneratedMappings.Register(mapper);

                using var database = new LiteDatabase(path, mapper);
                var collection = database.GetGeneratedCollection<ScalarRecord>("scalars");
                collection.Insert(new ScalarRecord
                {
                    Id = 7,
                    Enabled = true,
                    Count = -12,
                    Total = 9_876_543_210,
                    Ratio = 3.25d,
                    Amount = 1234.5678m,
                    Timestamp = expectedTimestamp,
                    CorrelationId = expectedGuid,
                    Payload = expectedPayload
                });

                var result = collection.FindById(7);

                Assert.IsNotNull(result);
                Assert.IsTrue(result.Enabled);
                Assert.AreEqual(-12, result.Count);
                Assert.AreEqual(9_876_543_210, result.Total);
                Assert.AreEqual(3.25d, result.Ratio);
                Assert.AreEqual(1234.5678m, result.Amount);
                Assert.AreEqual(expectedTimestamp, result.Timestamp);
                Assert.AreEqual(expectedGuid, result.CorrelationId);
                CollectionAssert.AreEqual(expectedPayload, result.Payload);
            }
            finally
            {
                File.Delete(path);
            }
        }

        [TestMethod]
        public void GetGeneratedCollection_SupportsCrudMultipleTypesAndDocumentCollections()
        {
            var path = GetDatabasePath();

            try
            {
                var mapper = new BsonMapper();
                LiteDbGeneratedMappings.Register(mapper);

                using var database = new LiteDatabase(path, mapper);
                var records = database.GetGeneratedCollection<GeneratedRecord>("records");
                var secondaryRecords = database.GetGeneratedCollection<SecondaryRecord>("secondaryRecords");
                var documents = database.GetCollection("documents");

                records.Insert(new GeneratedRecord { Name = "before", Values = [] });
                secondaryRecords.Insert(new SecondaryRecord { Id = 10, Description = "secondary" });
                documents.Insert(new BsonDocument { ["_id"] = 100, ["kind"] = "document" });

                var record = records.FindAll().Single();
                record.Name = "after";

                Assert.AreEqual(1, records.Count());
                Assert.IsTrue(records.Update(record));
                Assert.AreEqual("after", records.FindById(record.Id).Name);
                Assert.AreEqual("secondary", secondaryRecords.FindById(10).Description);
                Assert.AreEqual("document", documents.FindById(100)["kind"].AsString);
                Assert.IsTrue(records.Delete(record.Id));
                Assert.AreEqual(0, records.Count());
            }
            finally
            {
                File.Delete(path);
            }
        }

        [TestMethod]
        public void GetGeneratedCollection_UsesRequestedAutoIdWhenModelHasNoId()
        {
            var path = GetDatabasePath();

            try
            {
                var mapper = new BsonMapper();
                LiteDbGeneratedMappings.Register(mapper);

                using var database = new LiteDatabase(path, mapper);
                var collection = database.GetGeneratedCollection<NoIdRecord>("generatedAutoIds", BsonAutoId.Int32);
                var id = collection.Insert(new NoIdRecord { Name = "generated-id" });

                var result = collection.FindById(id);

                Assert.AreEqual(1, id.AsInt32);
                Assert.IsNotNull(result);
                Assert.AreEqual("generated-id", result.Name);
            }
            finally
            {
                File.Delete(path);
            }
        }

        [TestMethod]
        public void GetGeneratedCollection_RoundTripsUsingStreamBackedDatabase()
        {
            var mapper = new BsonMapper();
            LiteDbGeneratedMappings.Register(mapper);

            using var stream = new MemoryStream();
            using var database = new LiteDatabase(stream, mapper);
            var collection = database.GetGeneratedCollection<GeneratedRecord>("streamRecords");
            collection.Insert(new GeneratedRecord { Name = "stream", Values = ["value"] });

            var result = collection.FindById(1);

            Assert.IsNotNull(result);
            Assert.AreEqual("stream", result.Name);
            CollectionAssert.AreEqual(new[] { "value" }, result.Values);
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
    public sealed class NullableListRecord
    {
        public int Id { get; set; }

        public List<string>? Values { get; set; }
    }

    [BsonSourceGenerated]
    public sealed class ConventionIdRecord
    {
        public int ConventionIdRecordId { get; set; }

        [BsonField(Name = "stored_name")]
        public string Name { get; set; } = string.Empty;

        [BsonIgnore]
        public string? IgnoredText { get; set; }

        [BsonIgnore]
        public int IgnoredNumber { get; set; }
    }

    [BsonSourceGenerated]
    public sealed class ScalarRecord
    {
        public int Id { get; set; }
        public bool Enabled { get; set; }
        public int Count { get; set; }
        public long Total { get; set; }
        public double Ratio { get; set; }
        public decimal Amount { get; set; }
        public DateTime Timestamp { get; set; }
        public Guid CorrelationId { get; set; }
        public byte[] Payload { get; set; } = [];
    }

    [BsonSourceGenerated]
    public sealed class SecondaryRecord
    {
        public int Id { get; set; }
        public string Description { get; set; } = string.Empty;
    }

    [BsonSourceGenerated]
    public sealed class NoIdRecord
    {
        public string Name { get; set; } = string.Empty;
    }

    [BsonSourceGenerated]
    public sealed class ExplicitIdRecord
    {
        [BsonId(false)]
        public int Id { get; set; }

        public string Name { get; set; } = string.Empty;
    }
}
