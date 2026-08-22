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
        public void GetExpression_EvaluatesStaticMembersWithoutRuntimeCodeGeneration()
        {
            var mapper = new BsonMapper();
            var document = mapper.ToDocument(new GeneratedRecord { Id = 0, Name = "interpreter" });
            var expression = mapper.GetExpression<GeneratedRecord, bool>(record => record.Id < DateTime.Today.Day + 1);

            var results = expression.Execute(document).ToArray();

            Assert.AreEqual(1, results.Length);
            Assert.IsTrue(results[0].AsBoolean);
        }

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

        [TestMethod]
        public void GetGeneratedCollection_RoundTripsDateTimeOffsetAndNullableValues()
        {
            var path = GetDatabasePath();
            var expectedOccurredAt = new DateTimeOffset(2024, 6, 7, 8, 9, 10, TimeSpan.FromHours(5.5)).AddTicks(4321);

            try
            {
                var mapper = new BsonMapper { SerializeNullValues = true };
                LiteDbGeneratedMappings.Register(mapper);

                using var database = new LiteDatabase(path, mapper);
                var collection = database.GetGeneratedCollection<DateTimeOffsetRecord>("dateTimeOffsets");
                collection.Insert(new DateTimeOffsetRecord
                {
                    Id = 1,
                    OccurredAt = expectedOccurredAt,
                    DeliveredAt = null
                });

                var result = collection.FindById(1);
                var document = database.GetCollection("dateTimeOffsets").FindById(1);
                var occurredAt = document[nameof(DateTimeOffsetRecord.OccurredAt)].AsDocument;

                Assert.IsNotNull(result);
                Assert.IsTrue(expectedOccurredAt.EqualsExact(result.OccurredAt));
                Assert.IsNull(result.DeliveredAt);
                Assert.AreEqual(expectedOccurredAt.Ticks, occurredAt["DateTime"].AsInt64);
                Assert.AreEqual(expectedOccurredAt.Offset.Ticks, occurredAt["Offset"].AsInt64);
                Assert.IsTrue(document.ContainsKey(nameof(DateTimeOffsetRecord.DeliveredAt)));
                Assert.IsTrue(document[nameof(DateTimeOffsetRecord.DeliveredAt)].IsNull);
            }
            finally
            {
                File.Delete(path);
            }
        }

        [TestMethod]
        public void GetGeneratedCollection_RoundTripsNativeScalarBoundaries()
        {
            var path = GetDatabasePath();
            var expectedObjectId = new ObjectId("64c61e5f18a9421a8862c71c");
            var expectedDateTime = new DateTime(2024, 6, 7, 8, 9, 10, 123, DateTimeKind.Utc);
            var expectedDateTimeOffset = new DateTimeOffset(2024, 6, 8, 9, 10, 11, TimeSpan.FromHours(-3)).AddTicks(4321);
            var expectedPayload = new byte[] { 0, 1, 127, 128, 255 };

            try
            {
                var mapper = new BsonMapper();
                LiteDbGeneratedMappings.Register(mapper);

                using var database = new LiteDatabase(path, mapper);
                var collection = database.GetGeneratedCollection<NativeScalarRecord>("nativeScalars");
                collection.Insert(new NativeScalarRecord
                {
                    Id = 1,
                    ByteValue = 200,
                    SignedByteValue = -100,
                    Character = '\u03BB',
                    SignedShort = -12_345,
                    UnsignedShort = 54_321,
                    UnsignedInteger = 3_000_000_000U,
                    UnsignedLong = 9_000_000_000_000_000_000UL,
                    SingleValue = 123.5f,
                    State = NativeScalarState.Captured,
                    ObjectId = expectedObjectId,
                    Timestamp = expectedDateTime,
                    TimestampWithOffset = expectedDateTimeOffset,
                    Payload = expectedPayload,
                    CorrelationId = new Guid("09e72680-2f4f-4eb3-a70c-f27d489b6068"),
                    Name = "native-scalars"
                });

                var result = collection.FindById(1);

                Assert.IsNotNull(result);
                Assert.AreEqual((byte)200, result.ByteValue);
                Assert.AreEqual((sbyte)-100, result.SignedByteValue);
                Assert.AreEqual('\u03BB', result.Character);
                Assert.AreEqual((short)-12_345, result.SignedShort);
                Assert.AreEqual((ushort)54_321, result.UnsignedShort);
                Assert.AreEqual(3_000_000_000U, result.UnsignedInteger);
                Assert.AreEqual(9_000_000_000_000_000_000UL, result.UnsignedLong);
                Assert.AreEqual(123.5f, result.SingleValue);
                Assert.AreEqual(NativeScalarState.Captured, result.State);
                Assert.AreEqual(expectedObjectId, result.ObjectId);
                Assert.AreEqual(expectedDateTime, result.Timestamp.ToUniversalTime());
                Assert.IsTrue(expectedDateTimeOffset.EqualsExact(result.TimestampWithOffset));
                CollectionAssert.AreEqual(expectedPayload, result.Payload);
                Assert.AreEqual(new Guid("09e72680-2f4f-4eb3-a70c-f27d489b6068"), result.CorrelationId);
                Assert.AreEqual("native-scalars", result.Name);
            }
            finally
            {
                File.Delete(path);
            }
        }

        [TestMethod]
        public void GetGeneratedCollection_RoundTripsNullableScalars()
        {
            var path = GetDatabasePath();
            var expectedTimestamp = new DateTime(2024, 7, 6, 8, 9, 10, 123, DateTimeKind.Utc);
            var expectedCorrelationId = new Guid("5e3e59bf-c079-46cf-98f6-8287ab7c69cc");

            try
            {
                var mapper = new BsonMapper { SerializeNullValues = true };
                LiteDbGeneratedMappings.Register(mapper);

                using var database = new LiteDatabase(path, mapper);
                var collection = database.GetGeneratedCollection<NullableScalarRecord>("nullableScalars");
                collection.Insert(new NullableScalarRecord
                {
                    Id = 1,
                    ProcessId = 8128,
                    IsElevated = false,
                    State = NativeScalarState.Captured,
                    CorrelationId = expectedCorrelationId,
                    RecordedAt = expectedTimestamp
                });
                collection.Insert(new NullableScalarRecord { Id = 2 });

                var populated = collection.FindById(1);
                var nulls = collection.FindById(2);
                var nullDocument = database.GetCollection("nullableScalars").FindById(2);

                Assert.IsNotNull(populated);
                Assert.AreEqual(8128, populated.ProcessId);
                Assert.AreEqual(false, populated.IsElevated);
                Assert.AreEqual(NativeScalarState.Captured, populated.State);
                Assert.AreEqual(expectedCorrelationId, populated.CorrelationId);
                Assert.IsNotNull(populated.RecordedAt);
                Assert.AreEqual(expectedTimestamp, populated.RecordedAt.Value.ToUniversalTime());
                Assert.IsNotNull(nulls);
                Assert.IsNull(nulls.ProcessId);
                Assert.IsNull(nulls.IsElevated);
                Assert.IsNull(nulls.State);
                Assert.IsNull(nulls.CorrelationId);
                Assert.IsNull(nulls.RecordedAt);
                Assert.IsTrue(nullDocument[nameof(NullableScalarRecord.ProcessId)].IsNull);
                Assert.IsTrue(nullDocument[nameof(NullableScalarRecord.IsElevated)].IsNull);
                Assert.IsTrue(nullDocument[nameof(NullableScalarRecord.State)].IsNull);
                Assert.IsTrue(nullDocument[nameof(NullableScalarRecord.CorrelationId)].IsNull);
                Assert.IsTrue(nullDocument[nameof(NullableScalarRecord.RecordedAt)].IsNull);
            }
            finally
            {
                File.Delete(path);
            }
        }

        [TestMethod]
        public void GetGeneratedCollection_RoundTripsDynamicDictionaries()
        {
            var path = GetDatabasePath();
            var expectedObjectId = new ObjectId("64c61e5f18a9421a8862c71c");
            var expectedTimestamp = new DateTime(2024, 7, 8, 9, 10, 11, 123, DateTimeKind.Utc);
            var expectedCorrelationId = new Guid("cdac4923-b7b0-4e3a-af23-c0f65522af55");
            var expectedPayload = new byte[] { 0, 127, 255 };

            try
            {
                var mapper = new BsonMapper { SerializeNullValues = true };
                LiteDbGeneratedMappings.Register(mapper);

                using var database = new LiteDatabase(path, mapper);
                var collection = database.GetGeneratedCollection<DynamicDictionaryRecord>("dynamicDictionaries");
                collection.Insert(new DynamicDictionaryRecord
                {
                    Id = 1,
                    Fields = new Dictionary<string, object?>
                    {
                        ["message"] = "payload",
                        ["attempt"] = 3,
                        ["total"] = 9_000_000_000L,
                        ["enabled"] = false,
                        ["amount"] = 12.5m,
                        ["timestamp"] = expectedTimestamp,
                        ["objectId"] = expectedObjectId,
                        ["correlationId"] = expectedCorrelationId,
                        ["payload"] = expectedPayload,
                        ["missing"] = null,
                        ["nested"] = new Dictionary<string, object?>
                        {
                            ["inner"] = "value",
                            ["number"] = 7
                        },
                        ["items"] = new object?[] { "first", 2, null }
                    }
                });

                collection.Insert(new DynamicDictionaryRecord { Id = 2, Fields = null! });

                var result = collection.FindById(1);
                var nullDictionary = collection.FindById(2);
                var document = database.GetCollection("dynamicDictionaries").FindById(1);
                var nullDocument = database.GetCollection("dynamicDictionaries").FindById(2);

                Assert.IsNotNull(result);
                Assert.IsNotNull(result.Fields);
                Assert.AreEqual("payload", result.Fields["message"]);
                Assert.AreEqual(3, result.Fields["attempt"]);
                Assert.AreEqual(9_000_000_000L, result.Fields["total"]);
                Assert.AreEqual(false, result.Fields["enabled"]);
                Assert.AreEqual(12.5m, result.Fields["amount"]);
                Assert.IsInstanceOfType(result.Fields["timestamp"], typeof(DateTime));
                Assert.AreEqual(expectedTimestamp, ((DateTime)result.Fields["timestamp"]!).ToUniversalTime());
                Assert.AreEqual(expectedObjectId, result.Fields["objectId"]);
                Assert.AreEqual(expectedCorrelationId, result.Fields["correlationId"]);
                CollectionAssert.AreEqual(expectedPayload, (byte[])result.Fields["payload"]!);
                Assert.IsNull(result.Fields["missing"]);
                var nested = (Dictionary<string, object?>)result.Fields["nested"]!;
                Assert.AreEqual("value", nested["inner"]);
                Assert.AreEqual(7, nested["number"]);
                var items = (object?[])result.Fields["items"]!;
                Assert.AreEqual("first", items[0]);
                Assert.AreEqual(2, items[1]);
                Assert.IsNull(items[2]);
                Assert.AreEqual(3, document[nameof(DynamicDictionaryRecord.Fields)].AsDocument["attempt"].AsInt32);
                Assert.IsTrue(document[nameof(DynamicDictionaryRecord.Fields)].AsDocument["missing"].IsNull);
                Assert.IsNotNull(nullDictionary);
                Assert.IsNull(nullDictionary.Fields);
                Assert.IsTrue(nullDocument[nameof(DynamicDictionaryRecord.Fields)].IsNull);
                var exception = Assert.ThrowsException<InvalidOperationException>(() => collection.Insert(new DynamicDictionaryRecord
                {
                    Id = 3,
                    Fields = new Dictionary<string, object?> { ["unsupported"] = new UnsupportedDynamicDictionaryValue() }
                }));
                StringAssert.Contains(exception.Message, "Unsupported dynamic dictionary value type");
            }
            finally
            {
                File.Delete(path);
            }
        }

        [TestMethod]
        public void GetGeneratedCollection_RoundTripsStringArrays()
        {
            var path = GetDatabasePath();

            try
            {
                var mapper = new BsonMapper { SerializeNullValues = true };
                LiteDbGeneratedMappings.Register(mapper);

                using var database = new LiteDatabase(path, mapper);
                var collection = database.GetGeneratedCollection<StringArrayRecord>("stringArrays");
                collection.Insert(new StringArrayRecord { Id = 1, StreamNames = ["primary", "metadata"] });
                collection.Insert(new StringArrayRecord { Id = 2, StreamNames = null });
                collection.Insert(new StringArrayRecord { Id = 3, StreamNames = [] });

                var populated = collection.FindById(1);
                var nulls = collection.FindById(2);
                var empty = collection.FindById(3);
                var populatedDocument = database.GetCollection("stringArrays").FindById(1);
                var nullDocument = database.GetCollection("stringArrays").FindById(2);

                Assert.IsNotNull(populated);
                CollectionAssert.AreEqual(new[] { "primary", "metadata" }, populated.StreamNames);
                Assert.IsNotNull(nulls);
                Assert.IsNull(nulls.StreamNames);
                Assert.IsNotNull(empty);
                Assert.IsNotNull(empty.StreamNames);
                Assert.AreEqual(0, empty.StreamNames.Length);
                Assert.AreEqual(2, populatedDocument[nameof(StringArrayRecord.StreamNames)].AsArray.Count);
                Assert.IsTrue(nullDocument[nameof(StringArrayRecord.StreamNames)].IsNull);
            }
            finally
            {
                File.Delete(path);
            }
        }

        [TestMethod]
        public void GetGeneratedCollection_ExcludesComputedGetterOnlyProperties()
        {
            var path = GetDatabasePath();

            try
            {
                var mapper = new BsonMapper();
                LiteDbGeneratedMappings.Register(mapper);

                using var database = new LiteDatabase(path, mapper);
                var collection = database.GetGeneratedCollection<ComputedRecord>("computedRecords");
                collection.Insert(new ComputedRecord
                {
                    Id = 1,
                    NodeType = "file",
                    ContentHash = "abc123"
                });

                var result = collection.FindById(1);
                var document = database.GetCollection("computedRecords").FindById(1);

                Assert.IsNotNull(result);
                Assert.AreEqual("file|abc123", result.Fingerprint);
                Assert.IsFalse(document.ContainsKey(nameof(ComputedRecord.Fingerprint)));
            }
            finally
            {
                File.Delete(path);
            }
        }

        [TestMethod]
        public void GetGeneratedCollection_RoundTripsInheritedProperties()
        {
            var path = GetDatabasePath();

            try
            {
                var mapper = new BsonMapper();
                LiteDbGeneratedMappings.Register(mapper);

                using var database = new LiteDatabase(path, mapper);
                var collection = database.GetGeneratedCollection<InheritedRecord>("inheritedRecords");
                collection.Insert(new InheritedRecord
                {
                    BaseId = 42,
                    BaseName = "base-value",
                    BaseTags = ["first", "second"],
                    IgnoredBaseValue = "not persisted",
                    DerivedName = "derived-value"
                });

                var result = collection.FindById(42);
                var document = database.GetCollection("inheritedRecords").FindById(42);

                Assert.IsNotNull(result);
                Assert.AreEqual(42, result.BaseId);
                Assert.AreEqual("base-value", result.BaseName);
                CollectionAssert.AreEqual(new[] { "first", "second" }, result.BaseTags);
                Assert.IsNull(result.IgnoredBaseValue);
                Assert.AreEqual("derived-value", result.DerivedName);
                Assert.AreEqual(42, document["_id"].AsInt32);
                Assert.AreEqual("base-value", document["base_name"].AsString);
                Assert.AreEqual(2, document[nameof(InheritedRecord.BaseTags)].AsArray.Count);
                Assert.IsFalse(document.ContainsKey(nameof(InheritedRecord.IgnoredBaseValue)));
                Assert.AreEqual("derived-value", document[nameof(InheritedRecord.DerivedName)].AsString);
            }
            finally
            {
                File.Delete(path);
            }
        }

        [TestMethod]
        public void GetGeneratedCollection_RoundTripsDateTimeOffsetBoundariesAndRawShape()
        {
            var path = GetDatabasePath();
            var expectedPositiveOffset = new DateTimeOffset(2024, 7, 8, 9, 10, 11, TimeSpan.FromHours(5.5)).AddTicks(1234);
            var expectedNegativeOffset = new DateTimeOffset(2024, 7, 9, 10, 11, 12, TimeSpan.FromHours(-8)).AddTicks(4321);
            var expectedNullableOffset = new DateTimeOffset(2024, 7, 10, 11, 12, 13, TimeSpan.Zero).AddTicks(9876);

            try
            {
                var mapper = new BsonMapper { SerializeNullValues = true };
                LiteDbGeneratedMappings.Register(mapper);

                using var database = new LiteDatabase(path, mapper);
                var collection = database.GetGeneratedCollection<DateTimeOffsetBoundaryRecord>("dateTimeOffsetBoundaries");
                collection.Insert(new DateTimeOffsetBoundaryRecord
                {
                    Id = 1,
                    PositiveOffset = expectedPositiveOffset,
                    NegativeOffset = expectedNegativeOffset,
                    NullableOffset = expectedNullableOffset
                });

                var result = collection.FindById(1);
                var document = database.GetCollection("dateTimeOffsetBoundaries").FindById(1);

                Assert.IsNotNull(result);
                Assert.IsTrue(expectedPositiveOffset.EqualsExact(result.PositiveOffset));
                Assert.IsTrue(expectedNegativeOffset.EqualsExact(result.NegativeOffset));
                Assert.IsNotNull(result.NullableOffset);
                Assert.IsTrue(expectedNullableOffset.EqualsExact(result.NullableOffset.Value));
                AssertDateTimeOffsetDocument(document[nameof(DateTimeOffsetBoundaryRecord.PositiveOffset)], expectedPositiveOffset);
                AssertDateTimeOffsetDocument(document[nameof(DateTimeOffsetBoundaryRecord.NegativeOffset)], expectedNegativeOffset);
                AssertDateTimeOffsetDocument(document[nameof(DateTimeOffsetBoundaryRecord.NullableOffset)], expectedNullableOffset);
            }
            finally
            {
                File.Delete(path);
            }
        }

        [TestMethod]
        public void GetGeneratedCollection_RoundTripsNullableScalarBoundariesAndAbsentMembers()
        {
            var path = GetDatabasePath();
            var expectedDateTimeOffset = new DateTimeOffset(2024, 7, 11, 12, 13, 14, TimeSpan.FromHours(-3)).AddTicks(5678);

            try
            {
                var mapper = new BsonMapper();
                LiteDbGeneratedMappings.Register(mapper);

                using var database = new LiteDatabase(path, mapper);
                var collection = database.GetGeneratedCollection<NullableScalarBoundaryRecord>("nullableScalarBoundaries");
                collection.Insert(new NullableScalarBoundaryRecord
                {
                    Id = 1,
                    SignedShort = -12_345,
                    UnsignedLong = 9_000_000_000_000_000_000UL,
                    Ratio = 123.5d,
                    Amount = 456.789m,
                    TimestampWithOffset = expectedDateTimeOffset
                });
                collection.Insert(new NullableScalarBoundaryRecord { Id = 2 });

                var populated = collection.FindById(1);
                var absent = collection.FindById(2);
                var absentDocument = database.GetCollection("nullableScalarBoundaries").FindById(2);

                Assert.IsNotNull(populated);
                Assert.AreEqual((short)-12_345, populated.SignedShort);
                Assert.AreEqual(9_000_000_000_000_000_000UL, populated.UnsignedLong);
                Assert.AreEqual(123.5d, populated.Ratio);
                Assert.AreEqual(456.789m, populated.Amount);
                Assert.IsNotNull(populated.TimestampWithOffset);
                Assert.IsTrue(expectedDateTimeOffset.EqualsExact(populated.TimestampWithOffset.Value));
                Assert.IsNotNull(absent);
                Assert.IsNull(absent.SignedShort);
                Assert.IsNull(absent.UnsignedLong);
                Assert.IsNull(absent.Ratio);
                Assert.IsNull(absent.Amount);
                Assert.IsNull(absent.TimestampWithOffset);
                Assert.IsFalse(absentDocument.ContainsKey(nameof(NullableScalarBoundaryRecord.SignedShort)));
                Assert.IsFalse(absentDocument.ContainsKey(nameof(NullableScalarBoundaryRecord.UnsignedLong)));
                Assert.IsFalse(absentDocument.ContainsKey(nameof(NullableScalarBoundaryRecord.Ratio)));
                Assert.IsFalse(absentDocument.ContainsKey(nameof(NullableScalarBoundaryRecord.Amount)));
                Assert.IsFalse(absentDocument.ContainsKey(nameof(NullableScalarBoundaryRecord.TimestampWithOffset)));
            }
            finally
            {
                File.Delete(path);
            }
        }

        [TestMethod]
        public void GetGeneratedCollection_RoundTripsMultiLevelInheritedProperties()
        {
            var path = GetDatabasePath();

            try
            {
                var mapper = new BsonMapper();
                LiteDbGeneratedMappings.Register(mapper);

                using var database = new LiteDatabase(path, mapper);
                var collection = database.GetGeneratedCollection<MultiLevelInheritedRecord>("multiLevelInheritedRecords");
                collection.Insert(new MultiLevelInheritedRecord
                {
                    RootId = 73,
                    Origin = "grandparent",
                    ParentName = "parent",
                    IgnoredParentValue = "not persisted",
                    DerivedName = "derived"
                });

                var result = collection.FindById(73);
                var document = database.GetCollection("multiLevelInheritedRecords").FindById(73);

                Assert.IsNotNull(result);
                Assert.AreEqual(73, result.RootId);
                Assert.AreEqual("grandparent", result.Origin);
                Assert.AreEqual("parent", result.ParentName);
                Assert.IsNull(result.IgnoredParentValue);
                Assert.AreEqual("derived", result.DerivedName);
                Assert.AreEqual(73, document["_id"].AsInt32);
                Assert.AreEqual("grandparent", document["origin"].AsString);
                Assert.AreEqual("parent", document[nameof(MultiLevelInheritedRecord.ParentName)].AsString);
                Assert.IsFalse(document.ContainsKey(nameof(MultiLevelInheritedRecord.IgnoredParentValue)));
                Assert.AreEqual("derived", document[nameof(MultiLevelInheritedRecord.DerivedName)].AsString);
            }
            finally
            {
                File.Delete(path);
            }
        }

        [TestMethod]
        public void GetGeneratedCollection_ExcludesMultipleComputedProjectionsAndRecomputesThem()
        {
            var path = GetDatabasePath();

            try
            {
                var mapper = new BsonMapper();
                LiteDbGeneratedMappings.Register(mapper);

                using var database = new LiteDatabase(path, mapper);
                var collection = database.GetGeneratedCollection<ComputedProjectionRecord>("computedProjectionRecords");
                collection.Insert(new ComputedProjectionRecord
                {
                    Id = 1,
                    NodeType = "file",
                    Values = ["content", "acl", "streams"]
                });

                var result = collection.FindById(1);
                var document = database.GetCollection("computedProjectionRecords").FindById(1);

                Assert.IsNotNull(result);
                Assert.AreEqual("file|content|acl|streams", result.Fingerprint);
                Assert.AreEqual(3, result.ValueCount);
                Assert.AreEqual("content,acl,streams", result.ValueSummary);
                Assert.IsFalse(document.ContainsKey(nameof(ComputedProjectionRecord.Fingerprint)));
                Assert.IsFalse(document.ContainsKey(nameof(ComputedProjectionRecord.ValueCount)));
                Assert.IsFalse(document.ContainsKey(nameof(ComputedProjectionRecord.ValueSummary)));
            }
            finally
            {
                File.Delete(path);
            }
        }

        [TestMethod]
        public void GetGeneratedCollection_RoundTripsStringArrayElementBoundariesAndOrder()
        {
            var path = GetDatabasePath();
            var expectedMany = Enumerable.Range(0, 64)
                .Select(index => index == 0 ? string.Empty : index == 63 ? "last" : $"value-{index:D2}")
                .ToArray();

            try
            {
                var mapper = new BsonMapper { SerializeNullValues = true };
                LiteDbGeneratedMappings.Register(mapper);

                using var database = new LiteDatabase(path, mapper);
                var collection = database.GetGeneratedCollection<StringArrayRecord>("stringArrayBoundaries");
                collection.Insert(new StringArrayRecord { Id = 1, StreamNames = [string.Empty] });
                collection.Insert(new StringArrayRecord { Id = 2, StreamNames = expectedMany });

                var single = collection.FindById(1);
                var many = collection.FindById(2);
                var singleDocument = database.GetCollection("stringArrayBoundaries").FindById(1);
                var manyDocument = database.GetCollection("stringArrayBoundaries").FindById(2);

                Assert.IsNotNull(single);
                CollectionAssert.AreEqual(new[] { string.Empty }, single.StreamNames);
                Assert.IsNotNull(many);
                CollectionAssert.AreEqual(expectedMany, many.StreamNames);
                Assert.AreEqual(string.Empty, singleDocument[nameof(StringArrayRecord.StreamNames)].AsArray[0].AsString);
                Assert.AreEqual(expectedMany.Length, manyDocument[nameof(StringArrayRecord.StreamNames)].AsArray.Count);
                Assert.AreEqual("last", manyDocument[nameof(StringArrayRecord.StreamNames)].AsArray[63].AsString);
            }
            finally
            {
                File.Delete(path);
            }
        }

        [TestMethod]
        public void GetGeneratedCollection_RoundTripsDynamicDictionaryBoundariesAndRejectsUnsupportedValues()
        {
            var path = GetDatabasePath();

            try
            {
                var mapper = new BsonMapper { SerializeNullValues = true };
                LiteDbGeneratedMappings.Register(mapper);

                using var database = new LiteDatabase(path, mapper);
                var collection = database.GetGeneratedCollection<DynamicDictionaryRecord>("dynamicDictionaryBoundaries");
                collection.Insert(new DynamicDictionaryRecord { Id = 1, Fields = [] });
                collection.Insert(new DynamicDictionaryRecord
                {
                    Id = 2,
                    Fields = new Dictionary<string, object?>
                    {
                        ["int32"] = 12,
                        ["int64"] = 9_000_000_000L,
                        ["double"] = 3.5d,
                        ["decimal"] = 6.75m,
                        ["rawDocument"] = new BsonDocument { ["kind"] = "raw" },
                        ["rawArray"] = new BsonArray { "first", 2 },
                        ["nestedArray"] = new object?[]
                        {
                            new Dictionary<string, object?> { ["inner"] = "value" },
                            new object?[] { "nested", 4 }
                        }
                    }
                });

                var empty = collection.FindById(1);
                var populated = collection.FindById(2);
                var document = database.GetCollection("dynamicDictionaryBoundaries").FindById(2);
                var fields = document[nameof(DynamicDictionaryRecord.Fields)].AsDocument;

                Assert.IsNotNull(empty);
                Assert.IsNotNull(empty.Fields);
                Assert.AreEqual(0, empty.Fields.Count);
                Assert.IsNotNull(populated);
                Assert.AreEqual(12, populated.Fields["int32"]);
                Assert.AreEqual(9_000_000_000L, populated.Fields["int64"]);
                Assert.AreEqual(3.5d, populated.Fields["double"]);
                Assert.AreEqual(6.75m, populated.Fields["decimal"]);
                var rawDocument = (Dictionary<string, object?>)populated.Fields["rawDocument"]!;
                Assert.AreEqual("raw", rawDocument["kind"]);
                var rawArray = (object?[])populated.Fields["rawArray"]!;
                Assert.AreEqual("first", rawArray[0]);
                Assert.AreEqual(2, rawArray[1]);
                var nestedArray = (object?[])populated.Fields["nestedArray"]!;
                Assert.AreEqual("value", ((Dictionary<string, object?>)nestedArray[0]!)["inner"]);
                Assert.AreEqual("nested", ((object?[])nestedArray[1]!)[0]);
                Assert.AreEqual(4, ((object?[])nestedArray[1]!)[1]);
                Assert.AreEqual(BsonType.Int32, fields["int32"].Type);
                Assert.AreEqual(BsonType.Int64, fields["int64"].Type);
                Assert.AreEqual(BsonType.Double, fields["double"].Type);
                Assert.AreEqual(BsonType.Decimal, fields["decimal"].Type);
                Assert.AreEqual("raw", fields["rawDocument"].AsDocument["kind"].AsString);
                Assert.AreEqual(2, fields["rawArray"].AsArray.Count);
                var pocoException = Assert.ThrowsException<InvalidOperationException>(() => collection.Insert(new DynamicDictionaryRecord
                {
                    Id = 3,
                    Fields = new Dictionary<string, object?> { ["unsupported"] = new UnsupportedDynamicDictionaryValue() }
                }));
                var dateTimeOffsetException = Assert.ThrowsException<InvalidOperationException>(() => collection.Insert(new DynamicDictionaryRecord
                {
                    Id = 4,
                    Fields = new Dictionary<string, object?>
                    {
                        ["unsupported"] = new DateTimeOffset(2024, 7, 12, 13, 14, 15, TimeSpan.Zero)
                    }
                }));
                StringAssert.Contains(pocoException.Message, "Unsupported dynamic dictionary value type");
                StringAssert.Contains(dateTimeOffsetException.Message, "Unsupported dynamic dictionary value type");
            }
            finally
            {
                File.Delete(path);
            }
        }

        private static void AssertDateTimeOffsetDocument(BsonValue value, DateTimeOffset expected)
        {
            Assert.IsTrue(value.IsDocument);
            Assert.AreEqual(expected.Ticks, value.AsDocument["DateTime"].AsInt64);
            Assert.AreEqual(expected.Offset.Ticks, value.AsDocument["Offset"].AsInt64);
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
    public sealed class NullableScalarRecord
    {
        public int Id { get; set; }
        public int? ProcessId { get; set; }
        public bool? IsElevated { get; set; }
        public NativeScalarState? State { get; set; }
        public Guid? CorrelationId { get; set; }
        public DateTime? RecordedAt { get; set; }
    }

    [BsonSourceGenerated]
    public sealed class DynamicDictionaryRecord
    {
        public int Id { get; set; }
        public Dictionary<string, object?> Fields { get; set; } = [];
    }

    internal sealed class UnsupportedDynamicDictionaryValue
    {
    }

    [BsonSourceGenerated]
    public sealed class StringArrayRecord
    {
        public int Id { get; set; }
        public string[]? StreamNames { get; set; }
    }

    [BsonSourceGenerated]
    public sealed class ComputedRecord
    {
        public int Id { get; set; }
        public string NodeType { get; set; } = string.Empty;
        public string ContentHash { get; set; } = string.Empty;
        public string Fingerprint => string.Join("|", NodeType, ContentHash);
    }

    public abstract class InheritedRecordBase
    {
        [BsonId(false)]
        public int BaseId { get; set; }

        [BsonField("base_name")]
        public string BaseName { get; set; } = string.Empty;

        public List<string> BaseTags { get; set; } = [];

        [BsonIgnore]
        public string? IgnoredBaseValue { get; set; }
    }

    [BsonSourceGenerated]
    public sealed class InheritedRecord : InheritedRecordBase
    {
        public string DerivedName { get; set; } = string.Empty;
    }

    [BsonSourceGenerated]
    public sealed class NativeScalarRecord
    {
        public int Id { get; set; }
        public byte ByteValue { get; set; }
        public sbyte SignedByteValue { get; set; }
        public char Character { get; set; }
        public short SignedShort { get; set; }
        public ushort UnsignedShort { get; set; }
        public uint UnsignedInteger { get; set; }
        public ulong UnsignedLong { get; set; }
        public float SingleValue { get; set; }
        public NativeScalarState State { get; set; }
        public ObjectId ObjectId { get; set; } = ObjectId.Empty;
        public DateTime Timestamp { get; set; }
        public DateTimeOffset TimestampWithOffset { get; set; }
        public byte[] Payload { get; set; } = [];
        public Guid CorrelationId { get; set; }
        public string Name { get; set; } = string.Empty;
    }

    public enum NativeScalarState
    {
        Unknown = 0,
        Captured = 17
    }

    [BsonSourceGenerated]
    public sealed class DateTimeOffsetRecord
    {
        public int Id { get; set; }
        public DateTimeOffset OccurredAt { get; set; }
        public DateTimeOffset? DeliveredAt { get; set; }
    }

    [BsonSourceGenerated]
    public sealed class DateTimeOffsetBoundaryRecord
    {
        public int Id { get; set; }
        public DateTimeOffset PositiveOffset { get; set; }
        public DateTimeOffset NegativeOffset { get; set; }
        public DateTimeOffset? NullableOffset { get; set; }
    }

    [BsonSourceGenerated]
    public sealed class NullableScalarBoundaryRecord
    {
        public int Id { get; set; }
        public short? SignedShort { get; set; }
        public ulong? UnsignedLong { get; set; }
        public double? Ratio { get; set; }
        public decimal? Amount { get; set; }
        public DateTimeOffset? TimestampWithOffset { get; set; }
    }

    public abstract class MultiLevelInheritedGrandparent
    {
        [BsonId(false)]
        public int RootId { get; set; }

        [BsonField("origin")]
        public string Origin { get; set; } = string.Empty;
    }

    public abstract class MultiLevelInheritedParent : MultiLevelInheritedGrandparent
    {
        public string ParentName { get; set; } = string.Empty;

        [BsonIgnore]
        public string? IgnoredParentValue { get; set; }
    }

    [BsonSourceGenerated]
    public sealed class MultiLevelInheritedRecord : MultiLevelInheritedParent
    {
        public string DerivedName { get; set; } = string.Empty;
    }

    [BsonSourceGenerated]
    public sealed class ComputedProjectionRecord
    {
        public int Id { get; set; }
        public string NodeType { get; set; } = string.Empty;
        public List<string> Values { get; set; } = [];
        public string Fingerprint => string.Join("|", NodeType, string.Join("|", Values));
        public int ValueCount => Values.Count;
        public string ValueSummary => string.Join(",", Values);
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
