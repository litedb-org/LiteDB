using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

using LiteDB.Engine;
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
        public void GetGeneratedCollection_UsesRegisteredExecutionMapForScalarCrudWithoutGenericMapperConversion()
        {
            var path = GetDatabasePath();

            try
            {
                var mapper = new ThrowingConversionMapper();
                LiteDbGeneratedMappings.Register(mapper);

                using var database = new LiteDatabase(path, mapper);
                var collection = database.GetGeneratedCollection<PhaseBGeneratedRecord>("phaseB");
                var record = new PhaseBGeneratedRecord { Name = "inserted", Score = 7L };

                var id = collection.Insert(record);
                Assert.AreEqual(1, id.AsInt32);
                Assert.AreEqual(1, record.Id);

                record.Name = "updated";
                Assert.IsTrue(collection.Update(record));

                var read = collection.FindById(record.Id);
                Assert.IsNotNull(read);
                Assert.AreEqual("updated", read.Name);
                Assert.AreEqual(7L, read.Score);
                Assert.AreEqual(1, collection.Count());
                Assert.IsTrue(collection.Delete(record.Id));
                Assert.AreEqual(0, collection.Count());

                Assert.ThrowsException<NotSupportedException>(() => collection.FindAll());

                var ordinaryCollection = database.GetCollection<PhaseBGeneratedRecord>("ordinaryPhaseB");
                Assert.ThrowsException<AssertFailedException>(() =>
                    ordinaryCollection.Insert(new PhaseBGeneratedRecord { Name = "legacy", Score = 1L }));
            }
            finally
            {
                File.Delete(path);
            }
        }

        [TestMethod]
        public void GetGeneratedCollection_AutomaticallyRegistersC1ScalarExecutionMap()
        {
            var path = GetDatabasePath();

            try
            {
                var mapper = new ThrowingConversionMapper();
                LiteDbGeneratedMappings.Register(mapper);

                using var database = new LiteDatabase(path, mapper);
                var collection = database.GetGeneratedCollection<PhaseCScalarRecord>("phaseC");
                var record = new PhaseCScalarRecord { Score = 7 };

                var id = collection.Insert(record);
                var document = database.GetCollection("phaseC").FindById(id);
                Assert.AreEqual(1, id.AsInt32);
                Assert.AreEqual(1, record.Id);
                Assert.AreEqual(1, document["_id"].AsInt32);
                Assert.AreEqual(7, document[nameof(PhaseCScalarRecord.Score)].AsInt32);
                Assert.IsFalse(document.ContainsKey(nameof(PhaseCScalarRecord.Name)));

                record.Name = "updated";
                Assert.IsTrue(collection.Update(record));
                var read = collection.FindById(id);
                Assert.IsNotNull(read);
                Assert.AreEqual("updated", read.Name);
                Assert.AreEqual(7, read.Score);
                Assert.AreEqual(1, collection.Count());
                Assert.IsTrue(collection.Delete(id));
                Assert.AreEqual(0, collection.Count());
                Assert.ThrowsException<NotSupportedException>(() => collection.FindAll());
            }
            finally
            {
                File.Delete(path);
            }
        }

        [TestMethod]
        public void GetGeneratedCollection_AutomaticC1ScalarMap_AcceptsSerializeNullValuesChangedAfterAcquisition()
        {
            var path = GetDatabasePath();

            try
            {
                var mapper = new BsonMapper();
                LiteDbGeneratedMappings.Register(mapper);

                using var database = new LiteDatabase(path, mapper);
                var collection = database.GetGeneratedCollection<PhaseCScalarRecord>("phaseCNulls");
                mapper.SerializeNullValues = true;
                var id = collection.Insert(new PhaseCScalarRecord { Score = 8 });
                var document = database.GetCollection("phaseCNulls").FindById(id);

                Assert.IsTrue(document.ContainsKey(nameof(PhaseCScalarRecord.Name)));
                Assert.IsTrue(document[nameof(PhaseCScalarRecord.Name)].IsNull);
                Assert.IsNull(collection.FindById(id)?.Name);
            }
            finally
            {
                File.Delete(path);
            }
        }

        [TestMethod]
        public void GetGeneratedCollection_AutomaticC2ScalarMap_AppliesTrimWhitespaceAndEmptyStringToNullWithoutMapperFallback()
        {
            var path = GetDatabasePath();

            try
            {
                var mapper = new ThrowingConversionMapper
                {
                    TrimWhitespace = true,
                    EmptyStringToNull = true
                };
                LiteDbGeneratedMappings.Register(mapper);

                using var database = new LiteDatabase(path, mapper);
                var collection = database.GetGeneratedCollection<PhaseCScalarRecord>("phaseCTrimmedString");
                var id = collection.Insert(new PhaseCScalarRecord { Name = "  ", Score = 8 });
                var document = database.GetCollection("phaseCTrimmedString").FindById(id);

                Assert.IsTrue(document[nameof(PhaseCScalarRecord.Name)].IsNull);
                Assert.IsNull(collection.FindById(id)?.Name);
            }
            finally
            {
                File.Delete(path);
            }
        }

        [TestMethod]
        public void GetGeneratedCollection_AutomaticallyRegistersInheritedExecutionMap()
        {
            var path = GetDatabasePath();

            try
            {
                var mapper = new ThrowingConversionMapper();
                LiteDbGeneratedMappings.Register(mapper);

                using var database = new LiteDatabase(path, mapper);
                var collection = database.GetGeneratedCollection<PhaseBGeneratedRecord>("phaseBBridge");
                var id = collection.Insert(new PhaseBGeneratedRecord { Name = "generated", Score = 1L });
                var read = collection.FindById(id);

                Assert.IsNotNull(read);
                Assert.AreEqual("generated", read.Name);
                Assert.AreEqual(1L, read.Score);
            }
            finally
            {
                File.Delete(path);
            }
        }

        [TestMethod]
        public void GetGeneratedCollection_RejectsModelWithoutExecutionMap()
        {
            var mapper = new BsonMapper();
            mapper.RegisterGeneratedEntityMapper(new EntityMapper(typeof(UnsupportedExecutionRecord)));

            using var database = new LiteDatabase(new RecordingEngine(), mapper, false);
            var exception = Assert.ThrowsException<InvalidOperationException>(() =>
                database.GetGeneratedCollection<UnsupportedExecutionRecord>("unsupported"));

            StringAssert.Contains(exception.Message, "execution map");
        }

        [TestMethod]
        public void GetGeneratedCollection_AutomaticallyRegistersDirectMapForC2AttributeModel()
        {
            var path = GetDatabasePath();

            try
            {
                var mapper = new ThrowingConversionMapper();
                LiteDbGeneratedMappings.Register(mapper);

                using var database = new LiteDatabase(path, mapper);
                var collection = database.GetGeneratedCollection<PhaseCAttributedScalarRecord>("phaseCAttributes");
                var record = new PhaseCAttributedScalarRecord { Id = 1, Score = 8 };

                collection.Insert(record);

                var document = database.GetCollection("phaseCAttributes").FindById(1);
                var read = collection.FindById(1);
                Assert.AreEqual(1, document["_id"].AsInt32);
                Assert.AreEqual(8, document["score"].AsInt32);
                Assert.IsNotNull(read);
                Assert.AreEqual(1, read.Id);
                Assert.AreEqual(8, read.Score);
                Assert.AreEqual(2, document.Count);
                Assert.ThrowsException<NotSupportedException>(() => collection.FindAll());
            }
            finally
            {
                File.Delete(path);
            }
        }

        [TestMethod]
        public void GeneratedExecutionMap_IsMapperOwnedAndRejectsDuplicateRegistration()
        {
            var firstMapper = new BsonMapper();
            var secondMapper = new BsonMapper();
            LiteDbGeneratedMappings.Register(firstMapper);
            LiteDbGeneratedMappings.Register(secondMapper);

            Assert.ThrowsException<InvalidOperationException>(() =>
                firstMapper.RegisterGeneratedExecutionMap(CreatePhaseBExecutionMap()));

            var path = GetDatabasePath();
            try
            {
                using (var firstDatabase = new LiteDatabase(path, firstMapper))
                {
                    var generated = firstDatabase.GetGeneratedCollection<PhaseBGeneratedRecord>("phaseB");
                    Assert.ThrowsException<NotSupportedException>(() => generated.FindAll());
                }

                using var secondDatabase = new LiteDatabase(path, secondMapper);
                var secondGenerated = secondDatabase.GetGeneratedCollection<PhaseBGeneratedRecord>("phaseB");
                Assert.ThrowsException<NotSupportedException>(() => secondGenerated.FindAll());
            }
            finally
            {
                File.Delete(path);
            }
        }

        [TestMethod]
        public void GeneratedExecutionMap_RequiresGeneratedEntityMapperAndSupportsNullSerialization()
        {
            var unregisteredMapper = new BsonMapper();
            Assert.ThrowsException<InvalidOperationException>(() =>
                unregisteredMapper.RegisterGeneratedExecutionMap(CreatePhaseBExecutionMap()));

            var mapper = new BsonMapper { SerializeNullValues = true };
            LiteDbGeneratedMappings.Register(mapper);

            var path = GetDatabasePath();
            try
            {
                using var database = new LiteDatabase(path, mapper);
                var collection = database.GetGeneratedCollection<PhaseBGeneratedRecord>("phaseB");
                var id = collection.Insert(new PhaseBGeneratedRecord { Name = null!, Score = 1L });
                var document = database.GetCollection("phaseB").FindById(id);

                Assert.IsTrue(document[nameof(PhaseBGeneratedRecord.Name)].IsNull);
            }
            finally
            {
                File.Delete(path);
            }
        }

        [TestMethod]
        public void GeneratedExecutionMap_RejectsCustomTypeConverters()
        {
            var mapper = new BsonMapper();
            mapper.RegisterType<Uri>(
                value => new BsonValue(value.AbsoluteUri),
                value => new Uri(value.AsString));
            LiteDbGeneratedMappings.Register(mapper);

            var path = GetDatabasePath();
            try
            {
                using var database = new LiteDatabase(path, mapper);
                var exception = Assert.ThrowsException<InvalidOperationException>(() =>
                    database.GetGeneratedCollection<PhaseBGeneratedRecord>("phaseB"));

                StringAssert.Contains(exception.Message, "does not support the active BsonMapper configuration");
            }
            finally
            {
                File.Delete(path);
            }
        }

        [TestMethod]
        public void GeneratedExecutionMap_AcceptsNullSerializationChangedAfterCollectionAcquisition()
        {
            var mapper = new BsonMapper();
            LiteDbGeneratedMappings.Register(mapper);

            var path = GetDatabasePath();
            try
            {
                using var database = new LiteDatabase(path, mapper);
                var collection = database.GetGeneratedCollection<PhaseBGeneratedRecord>("phaseB");

                mapper.SerializeNullValues = true;

                Assert.AreEqual(0, collection.Count());
            }
            finally
            {
                File.Delete(path);
            }
        }

        [DataTestMethod]
        [DataRow("query")]
        [DataRow("insert")]
        [DataRow("update")]
        [DataRow("upsert")]
        [DataRow("delete")]
        public void GeneratedExecution_RejectsPostAcquisitionConfigurationBeforeEngineAccess(string operation)
        {
            var mapper = new BsonMapper();
            LiteDbGeneratedMappings.Register(mapper);
            var engine = new RecordingEngine();

            using var database = new LiteDatabase(engine, mapper, false);
            var collection = database.GetGeneratedCollection<PhaseCScalarRecord>("guarded");
            mapper.OnDeserialization = (_, _, value) => value;

            Assert.ThrowsException<InvalidOperationException>(() =>
            {
                switch (operation)
                {
                    case "query": collection.FindById(1); break;
                    case "insert": collection.Insert(new PhaseCScalarRecord()); break;
                    case "update": collection.Update(new PhaseCScalarRecord { Id = 1 }); break;
                    case "upsert": collection.Upsert(new PhaseCScalarRecord()); break;
                    case "delete": collection.Delete(1); break;
                    default: Assert.Fail($"Unknown operation '{operation}'."); break;
                }
            });

            Assert.AreEqual(0, engine.DataAccessCount);
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
                var mapper = new ThrowingConversionMapper();
                LiteDbGeneratedMappings.Register(mapper);

                using var database = new LiteDatabase(path, mapper);
                var collection = database.GetGeneratedCollection<ExplicitIdRecord>("explicitIds");
                collection.Insert(new ExplicitIdRecord { Id = 42, Name = "manual" });

                var result = collection.FindById(42);

                Assert.IsNotNull(result);
                Assert.AreEqual("manual", result.Name);
                Assert.ThrowsException<NotSupportedException>(() => collection.FindAll());
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
                var mapper = new ThrowingConversionMapper { SerializeNullValues = true };
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
                var mapper = new ThrowingConversionMapper();
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
                Assert.ThrowsException<NotSupportedException>(() => collection.FindAll());
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
                var mapper = new ThrowingConversionMapper();
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

                var record = new GeneratedRecord { Name = "before", Values = [] };
                records.Insert(record);
                secondaryRecords.Insert(new SecondaryRecord { Id = 10, Description = "secondary" });
                documents.Insert(new BsonDocument { ["_id"] = 100, ["kind"] = "document" });

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
                var mapper = new ThrowingConversionMapper { SerializeNullValues = true };
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
                var occurredAt = document[nameof(DateTimeOffsetRecord.OccurredAt)];

                Assert.IsNotNull(result);
                AssertCanonicalDateTimeOffset(expectedOccurredAt, result.OccurredAt);
                Assert.IsNull(result.DeliveredAt);
                AssertCanonicalDateTimeOffsetValue(occurredAt, expectedOccurredAt);
                Assert.IsTrue(document.ContainsKey(nameof(DateTimeOffsetRecord.DeliveredAt)));
                Assert.IsTrue(document[nameof(DateTimeOffsetRecord.DeliveredAt)].IsNull);
            }
            finally
            {
                File.Delete(path);
            }
        }

        [TestMethod]
        public void GetGeneratedCollection_RoundTripsMutableRecordClass()
        {
            var path = GetDatabasePath();

            try
            {
                var mapper = new BsonMapper();
                LiteDbGeneratedMappings.Register(mapper);

                using var database = new LiteDatabase(path, mapper);
                var collection = database.GetGeneratedCollection<MutableGeneratedRecord>("mutableRecords");
                collection.Insert(new MutableGeneratedRecord { Name = "record" });

                var result = collection.FindById(1);

                Assert.IsNotNull(result);
                Assert.AreEqual(1, result.Id);
                Assert.AreEqual("record", result.Name);
            }
            finally
            {
                File.Delete(path);
            }
        }

        [TestMethod]
        public void GetGeneratedCollection_UsesMostDerivedOverrideForInheritedId()
        {
            var path = GetDatabasePath();

            try
            {
                var mapper = new BsonMapper();
                LiteDbGeneratedMappings.Register(mapper);

                using var database = new LiteDatabase(path, mapper);
                var collection = database.GetGeneratedCollection<OverrideRecord>("overrideRecords");
                collection.Insert(new OverrideRecord { OverrideId = 42, Name = "override", Ignored = "not persisted" });

                var result = collection.FindById(42);
                var document = database.GetCollection("overrideRecords").FindById(42);

                Assert.IsNotNull(result);
                Assert.AreEqual(42, result.OverrideId);
                Assert.AreEqual(1, result.SetterCalls);
                Assert.AreEqual("override", result.Name);
                Assert.IsNull(result.Ignored);
                Assert.AreEqual(42, document["_id"].AsInt32);
                Assert.AreEqual("override", document["stored_name"].AsString);
                Assert.AreEqual(2, document.Count);
            }
            finally
            {
                File.Delete(path);
            }
        }

        [TestMethod]
        public void GeneratedAndRuntimeMappings_CrossReadDateTimeOffsetDocuments()
        {
            var path = GetDatabasePath();
            var legacyValue = new DateTimeOffset(2024, 7, 6, 8, 9, 10, TimeSpan.FromHours(5.5)).AddTicks(4321);
            var generatedValue = new DateTimeOffset(2024, 7, 7, 8, 9, 10, TimeSpan.FromHours(-8)).AddTicks(1234);
            var expectedLegacyUtcTicks = legacyValue.UtcDateTime.Ticks - (legacyValue.UtcDateTime.Ticks % TimeSpan.TicksPerMillisecond);

            try
            {
                using (var runtimeDatabase = new LiteDatabase(path, new BsonMapper()))
                {
                    runtimeDatabase.GetCollection<DateTimeOffsetRecord>("dateTimeOffsetCrossRead")
                        .Insert(new DateTimeOffsetRecord { Id = 1, OccurredAt = legacyValue });
                }

                using (var generatedDatabase = new LiteDatabase(path, CreateGeneratedMapper()))
                {
                    var generatedCollection = generatedDatabase.GetGeneratedCollection<DateTimeOffsetRecord>("dateTimeOffsetCrossRead");
                    var generatedRead = generatedCollection.FindById(1);

                    Assert.IsNotNull(generatedRead);
                    Assert.AreEqual(expectedLegacyUtcTicks, generatedRead.OccurredAt.UtcDateTime.Ticks);
                    Assert.AreEqual(TimeSpan.Zero, generatedRead.OccurredAt.Offset);

                    generatedCollection.Insert(new DateTimeOffsetRecord { Id = 2, OccurredAt = generatedValue });
                    generatedDatabase.GetCollection("dateTimeOffsetCrossRead").Insert(new BsonDocument
                    {
                        ["_id"] = 3,
                        [nameof(DateTimeOffsetRecord.OccurredAt)] = new BsonDocument
                        {
                            ["DateTime"] = generatedValue.Ticks,
                            ["Offset"] = generatedValue.Offset.Ticks
                        }
                    });
                    var legacyGeneratedRead = generatedCollection.FindById(3);

                    Assert.IsNotNull(legacyGeneratedRead);
                    Assert.IsTrue(generatedValue.EqualsExact(legacyGeneratedRead.OccurredAt));
                }

                using (var runtimeDatabase = new LiteDatabase(path, new BsonMapper()))
                {
                    var runtimeCollection = runtimeDatabase.GetCollection<DateTimeOffsetRecord>("dateTimeOffsetCrossRead");
                    var runtimeRead = runtimeCollection.FindById(2);
                    var documents = runtimeDatabase.GetCollection("dateTimeOffsetCrossRead");

                    Assert.IsNotNull(runtimeRead);
                    AssertCanonicalDateTimeOffset(generatedValue, runtimeRead.OccurredAt);
                    Assert.IsTrue(documents.FindById(1)[nameof(DateTimeOffsetRecord.OccurredAt)].IsDateTime);
                    Assert.IsTrue(documents.FindById(2)[nameof(DateTimeOffsetRecord.OccurredAt)].IsDateTime);
                }
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
                AssertCanonicalDateTimeOffset(expectedDateTimeOffset, result.TimestampWithOffset);
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
                var mapper = new ThrowingConversionMapper();
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
                Assert.ThrowsException<NotSupportedException>(() => collection.FindAll());
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
            var ordinaryGolden = new BsonMapper().ToDocument(new DateTimeOffsetBoundaryRecord
            {
                Id = 1,
                PositiveOffset = expectedPositiveOffset,
                NegativeOffset = expectedNegativeOffset,
                NullableOffset = expectedNullableOffset,
                Minimum = DateTimeOffset.MinValue,
                Maximum = DateTimeOffset.MaxValue
            });

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
                    NullableOffset = expectedNullableOffset,
                    Minimum = DateTimeOffset.MinValue,
                    Maximum = DateTimeOffset.MaxValue
                });

                var result = collection.FindById(1);
                var document = database.GetCollection("dateTimeOffsetBoundaries").FindById(1);

                Assert.IsNotNull(result);
                AssertCanonicalDateTimeOffset(expectedPositiveOffset, result.PositiveOffset);
                AssertCanonicalDateTimeOffset(expectedNegativeOffset, result.NegativeOffset);
                Assert.IsNotNull(result.NullableOffset);
                AssertCanonicalDateTimeOffset(expectedNullableOffset, result.NullableOffset.Value);
                AssertCanonicalDateTimeOffsetValue(document[nameof(DateTimeOffsetBoundaryRecord.PositiveOffset)], expectedPositiveOffset);
                AssertCanonicalDateTimeOffsetValue(document[nameof(DateTimeOffsetBoundaryRecord.NegativeOffset)], expectedNegativeOffset);
                AssertCanonicalDateTimeOffsetValue(document[nameof(DateTimeOffsetBoundaryRecord.NullableOffset)], expectedNullableOffset);
                AssertCanonicalDateTimeOffset(DateTimeOffset.MinValue, result.Minimum);
                AssertCanonicalDateTimeOffset(DateTimeOffset.MaxValue, result.Maximum);
                Assert.AreEqual(ordinaryGolden[nameof(DateTimeOffsetBoundaryRecord.PositiveOffset)], document[nameof(DateTimeOffsetBoundaryRecord.PositiveOffset)]);
                Assert.AreEqual(ordinaryGolden[nameof(DateTimeOffsetBoundaryRecord.NegativeOffset)], document[nameof(DateTimeOffsetBoundaryRecord.NegativeOffset)]);
                Assert.AreEqual(ordinaryGolden[nameof(DateTimeOffsetBoundaryRecord.NullableOffset)], document[nameof(DateTimeOffsetBoundaryRecord.NullableOffset)]);
                AssertCanonicalDateTimeOffsetValue(document[nameof(DateTimeOffsetBoundaryRecord.Minimum)], DateTimeOffset.MinValue);
                AssertCanonicalDateTimeOffsetValue(document[nameof(DateTimeOffsetBoundaryRecord.Maximum)], DateTimeOffset.MaxValue);
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
                AssertCanonicalDateTimeOffset(expectedDateTimeOffset, populated.TimestampWithOffset.Value);
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
            var normalizedMany = expectedMany.ToArray();
            normalizedMany[0] = null!;

            try
            {
                var mapper = new ThrowingConversionMapper { SerializeNullValues = true };
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
                CollectionAssert.AreEqual(new string?[] { null }, single.StreamNames);
                Assert.IsNotNull(many);
                CollectionAssert.AreEqual(normalizedMany, many.StreamNames);
                Assert.IsTrue(singleDocument[nameof(StringArrayRecord.StreamNames)].AsArray[0].IsNull);
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

        [TestMethod]
        public void GetGeneratedCollection_AutomaticC2ScalarMap_RoundTripsSupportedScalarMatrixWithoutMapperFallback()
        {
            var path = GetDatabasePath();
            var expectedObjectId = new ObjectId("64c61e5f18a9421a8862c71c");
            var expectedCorrelationId = new Guid("d29368bb-9669-4f84-9384-c8eb15caa0a8");
            var expectedTimestamp = new DateTime(2024, 8, 1, 12, 34, 56, 789, DateTimeKind.Utc);
            var expectedPayload = new byte[] { 0, 1, 127, 128, 255 };

            try
            {
                var mapper = new ThrowingConversionMapper
                {
                    SerializeNullValues = true,
                    TrimWhitespace = true,
                    EmptyStringToNull = true,
                    EnumAsInteger = false
                };
                LiteDbGeneratedMappings.Register(mapper);

                using var database = new LiteDatabase(path, mapper);
                var collection = database.GetGeneratedCollection<PhaseCScalarCompatibilityRecord>("phaseCScalarCompatibility");
                collection.Insert(new PhaseCScalarCompatibilityRecord
                {
                    Id = 1,
                    BooleanValue = true,
                    ByteValue = byte.MaxValue,
                    SignedByteValue = sbyte.MinValue,
                    Character = 'Q',
                    SignedShort = short.MinValue,
                    UnsignedShort = ushort.MaxValue,
                    SignedInteger = -123_456,
                    UnsignedInteger = uint.MaxValue,
                    SignedLong = -9_000_000_000L,
                    UnsignedLong = ulong.MaxValue,
                    SingleValue = 3.25f,
                    DoubleValue = 6.5d,
                    DecimalValue = 7.75m,
                    State = PhaseCScalarState.Completed,
                    Timestamp = expectedTimestamp,
                    ObjectId = expectedObjectId,
                    CorrelationId = expectedCorrelationId,
                    Payload = expectedPayload,
                    Name = "  scalar compatibility  ",
                    NullableInteger = null,
                    NullableState = null,
                    NullableObjectId = null,
                    NullableCorrelationId = null,
                    NullableTimestamp = null,
                    NullablePayload = null
                });

                var document = database.GetCollection("phaseCScalarCompatibility").FindById(1);
                Assert.AreEqual(BsonType.Boolean, document[nameof(PhaseCScalarCompatibilityRecord.BooleanValue)].Type);
                Assert.AreEqual(BsonType.Int32, document[nameof(PhaseCScalarCompatibilityRecord.ByteValue)].Type);
                Assert.AreEqual(BsonType.Int32, document[nameof(PhaseCScalarCompatibilityRecord.SignedByteValue)].Type);
                Assert.AreEqual(BsonType.String, document[nameof(PhaseCScalarCompatibilityRecord.Character)].Type);
                Assert.AreEqual(BsonType.Int32, document[nameof(PhaseCScalarCompatibilityRecord.SignedShort)].Type);
                Assert.AreEqual(BsonType.Int32, document[nameof(PhaseCScalarCompatibilityRecord.UnsignedShort)].Type);
                Assert.AreEqual(BsonType.Int32, document[nameof(PhaseCScalarCompatibilityRecord.SignedInteger)].Type);
                Assert.AreEqual(BsonType.Int64, document[nameof(PhaseCScalarCompatibilityRecord.UnsignedInteger)].Type);
                Assert.AreEqual(BsonType.Int64, document[nameof(PhaseCScalarCompatibilityRecord.SignedLong)].Type);
                Assert.AreEqual(BsonType.Int64, document[nameof(PhaseCScalarCompatibilityRecord.UnsignedLong)].Type);
                Assert.AreEqual(BsonType.Double, document[nameof(PhaseCScalarCompatibilityRecord.SingleValue)].Type);
                Assert.AreEqual(BsonType.Double, document[nameof(PhaseCScalarCompatibilityRecord.DoubleValue)].Type);
                Assert.AreEqual(BsonType.Decimal, document[nameof(PhaseCScalarCompatibilityRecord.DecimalValue)].Type);
                Assert.AreEqual(BsonType.String, document[nameof(PhaseCScalarCompatibilityRecord.State)].Type);
                Assert.AreEqual("Completed", document[nameof(PhaseCScalarCompatibilityRecord.State)].AsString);
                Assert.AreEqual(BsonType.DateTime, document[nameof(PhaseCScalarCompatibilityRecord.Timestamp)].Type);
                Assert.AreEqual(BsonType.ObjectId, document[nameof(PhaseCScalarCompatibilityRecord.ObjectId)].Type);
                Assert.AreEqual(BsonType.Guid, document[nameof(PhaseCScalarCompatibilityRecord.CorrelationId)].Type);
                Assert.AreEqual(BsonType.Binary, document[nameof(PhaseCScalarCompatibilityRecord.Payload)].Type);
                Assert.AreEqual("scalar compatibility", document[nameof(PhaseCScalarCompatibilityRecord.Name)].AsString);
                Assert.IsTrue(document[nameof(PhaseCScalarCompatibilityRecord.NullableInteger)].IsNull);
                Assert.IsTrue(document[nameof(PhaseCScalarCompatibilityRecord.NullableState)].IsNull);
                Assert.IsTrue(document[nameof(PhaseCScalarCompatibilityRecord.NullableObjectId)].IsNull);
                Assert.IsTrue(document[nameof(PhaseCScalarCompatibilityRecord.NullableCorrelationId)].IsNull);
                Assert.IsTrue(document[nameof(PhaseCScalarCompatibilityRecord.NullableTimestamp)].IsNull);
                Assert.IsTrue(document[nameof(PhaseCScalarCompatibilityRecord.NullablePayload)].IsNull);

                var actual = collection.FindById(1);
                Assert.IsNotNull(actual);
                Assert.AreEqual(true, actual.BooleanValue);
                Assert.AreEqual(byte.MaxValue, actual.ByteValue);
                Assert.AreEqual(sbyte.MinValue, actual.SignedByteValue);
                Assert.AreEqual('Q', actual.Character);
                Assert.AreEqual(short.MinValue, actual.SignedShort);
                Assert.AreEqual(ushort.MaxValue, actual.UnsignedShort);
                Assert.AreEqual(-123_456, actual.SignedInteger);
                Assert.AreEqual(uint.MaxValue, actual.UnsignedInteger);
                Assert.AreEqual(-9_000_000_000L, actual.SignedLong);
                Assert.AreEqual(ulong.MaxValue, actual.UnsignedLong);
                Assert.AreEqual(3.25f, actual.SingleValue);
                Assert.AreEqual(6.5d, actual.DoubleValue);
                Assert.AreEqual(7.75m, actual.DecimalValue);
                Assert.AreEqual(PhaseCScalarState.Completed, actual.State);
                Assert.AreEqual(expectedTimestamp, actual.Timestamp.ToUniversalTime());
                Assert.AreEqual(expectedObjectId, actual.ObjectId);
                Assert.AreEqual(expectedCorrelationId, actual.CorrelationId);
                CollectionAssert.AreEqual(expectedPayload, actual.Payload);
                Assert.AreEqual("scalar compatibility", actual.Name);
                Assert.IsNull(actual.NullableInteger);
                Assert.IsNull(actual.NullableState);
                Assert.IsNull(actual.NullableObjectId);
                Assert.IsNull(actual.NullableCorrelationId);
                Assert.IsNull(actual.NullableTimestamp);
                Assert.IsNull(actual.NullablePayload);

                mapper.EnumAsInteger = true;
                collection.Insert(new PhaseCScalarCompatibilityRecord { Id = 2, State = PhaseCScalarState.Ready });
                var integerEnumDocument = database.GetCollection("phaseCScalarCompatibility").FindById(2);
                Assert.AreEqual(BsonType.Int32, integerEnumDocument[nameof(PhaseCScalarCompatibilityRecord.State)].Type);
                Assert.AreEqual((int)PhaseCScalarState.Ready, integerEnumDocument[nameof(PhaseCScalarCompatibilityRecord.State)].AsInt32);
                Assert.AreEqual(PhaseCScalarState.Ready, collection.FindById(2)?.State);
            }
            finally
            {
                File.Delete(path);
            }
        }

        [TestMethod]
        public void GetGeneratedCollection_AutomaticC2ScalarMap_CrossReadsWithOrdinaryCollection()
        {
            var path = GetDatabasePath();
            var expectedObjectId = new ObjectId("64c61e5f18a9421a8862c71c");
            var expectedCorrelationId = new Guid("d29368bb-9669-4f84-9384-c8eb15caa0a8");
            var ordinaryTimestamp = new DateTime(2024, 8, 1, 12, 34, 56, 789, DateTimeKind.Utc);
            var ordinaryNullableTimestamp = new DateTime(2024, 8, 2, 12, 34, 56, 789, DateTimeKind.Utc);
            var directTimestamp = new DateTime(2024, 8, 3, 12, 34, 56, 789, DateTimeKind.Utc);
            var directNullableTimestamp = new DateTime(2024, 8, 4, 12, 34, 56, 789, DateTimeKind.Utc);

            try
            {
                var ordinaryWriterMapper = new BsonMapper
                {
                    SerializeNullValues = true,
                    TrimWhitespace = true,
                    EmptyStringToNull = true,
                    EnumAsInteger = false
                };
                LiteDbGeneratedMappings.Register(ordinaryWriterMapper);
                using (var database = new LiteDatabase(path, ordinaryWriterMapper))
                {
                    database.GetCollection<PhaseCScalarCompatibilityRecord>("phaseCScalarCrossRead").Insert(new PhaseCScalarCompatibilityRecord
                    {
                        Id = 1,
                        Name = "  ordinary writer  ",
                        UnsignedInteger = uint.MaxValue,
                        State = PhaseCScalarState.Completed,
                        NullableState = PhaseCScalarState.Ready,
                        ObjectId = expectedObjectId,
                        NullableObjectId = expectedObjectId,
                        CorrelationId = expectedCorrelationId,
                        NullableCorrelationId = expectedCorrelationId,
                        Payload = [1, 2, 3],
                        NullablePayload = [4, 5],
                        Timestamp = ordinaryTimestamp,
                        NullableTimestamp = ordinaryNullableTimestamp
                    });
                }

                var directMapper = new ThrowingConversionMapper
                {
                    SerializeNullValues = true,
                    TrimWhitespace = true,
                    EmptyStringToNull = true,
                    EnumAsInteger = false
                };
                LiteDbGeneratedMappings.Register(directMapper);
                using (var database = new LiteDatabase(path, directMapper))
                {
                    var direct = database.GetGeneratedCollection<PhaseCScalarCompatibilityRecord>("phaseCScalarCrossRead");
                    var ordinaryWritten = direct.FindById(1);
                    Assert.IsNotNull(ordinaryWritten);
                    Assert.AreEqual("ordinary writer", ordinaryWritten.Name);
                    Assert.AreEqual(uint.MaxValue, ordinaryWritten.UnsignedInteger);
                    Assert.AreEqual(PhaseCScalarState.Completed, ordinaryWritten.State);
                    Assert.AreEqual(PhaseCScalarState.Ready, ordinaryWritten.NullableState);
                    Assert.AreEqual(expectedObjectId, ordinaryWritten.ObjectId);
                    Assert.AreEqual(expectedObjectId, ordinaryWritten.NullableObjectId);
                    Assert.AreEqual(expectedCorrelationId, ordinaryWritten.CorrelationId);
                    Assert.AreEqual(expectedCorrelationId, ordinaryWritten.NullableCorrelationId);
                    Assert.AreEqual(ordinaryTimestamp, ordinaryWritten.Timestamp.ToUniversalTime());
                    Assert.IsTrue(ordinaryWritten.NullableTimestamp.HasValue);
                    Assert.AreEqual(ordinaryNullableTimestamp, ordinaryWritten.NullableTimestamp.Value.ToUniversalTime());
                    CollectionAssert.AreEqual(new byte[] { 1, 2, 3 }, ordinaryWritten.Payload);
                    CollectionAssert.AreEqual(new byte[] { 4, 5 }, ordinaryWritten.NullablePayload);

                    direct.Insert(new PhaseCScalarCompatibilityRecord
                    {
                        Id = 2,
                        Name = "  direct writer  ",
                        UnsignedInteger = uint.MaxValue - 1,
                        State = PhaseCScalarState.Ready,
                        NullableState = PhaseCScalarState.Completed,
                        ObjectId = expectedObjectId,
                        NullableObjectId = expectedObjectId,
                        CorrelationId = expectedCorrelationId,
                        NullableCorrelationId = expectedCorrelationId,
                        Payload = [6, 7, 8],
                        NullablePayload = [9],
                        Timestamp = directTimestamp,
                        NullableTimestamp = directNullableTimestamp
                    });
                }

                var ordinaryReaderMapper = new BsonMapper
                {
                    SerializeNullValues = true,
                    TrimWhitespace = true,
                    EmptyStringToNull = true,
                    EnumAsInteger = false
                };
                LiteDbGeneratedMappings.Register(ordinaryReaderMapper);
                using var readerDatabase = new LiteDatabase(path, ordinaryReaderMapper);
                var directWritten = readerDatabase.GetCollection<PhaseCScalarCompatibilityRecord>("phaseCScalarCrossRead").FindById(2);
                Assert.IsNotNull(directWritten);
                Assert.AreEqual("direct writer", directWritten.Name);
                Assert.AreEqual(uint.MaxValue - 1, directWritten.UnsignedInteger);
                Assert.AreEqual(PhaseCScalarState.Ready, directWritten.State);
                Assert.AreEqual(PhaseCScalarState.Completed, directWritten.NullableState);
                Assert.AreEqual(expectedObjectId, directWritten.ObjectId);
                Assert.AreEqual(expectedObjectId, directWritten.NullableObjectId);
                Assert.AreEqual(expectedCorrelationId, directWritten.CorrelationId);
                Assert.AreEqual(expectedCorrelationId, directWritten.NullableCorrelationId);
                Assert.AreEqual(directTimestamp, directWritten.Timestamp.ToUniversalTime());
                Assert.IsTrue(directWritten.NullableTimestamp.HasValue);
                Assert.AreEqual(directNullableTimestamp, directWritten.NullableTimestamp.Value.ToUniversalTime());
                CollectionAssert.AreEqual(new byte[] { 6, 7, 8 }, directWritten.Payload);
                CollectionAssert.AreEqual("\t"u8.ToArray(), directWritten.NullablePayload);
            }
            finally
            {
                File.Delete(path);
            }
        }

        [TestMethod]
        public void GetGeneratedCollection_AutomaticC2ScalarMap_ExecutesExplicitIdAndBatchWritesWithoutMapperFallback()
        {
            var path = GetDatabasePath();

            try
            {
                var mapper = new ThrowingConversionMapper();
                LiteDbGeneratedMappings.Register(mapper);

                using var database = new LiteDatabase(path, mapper);
                var collection = database.GetGeneratedCollection<PhaseCScalarRecord>("phaseCScalarBatchWrites");
                var explicitInsert = new PhaseCScalarRecord { Id = 900, Name = "explicit-insert", Score = 1 };
                collection.Insert(41, explicitInsert);

                Assert.AreEqual(900, explicitInsert.Id);
                Assert.AreEqual("explicit-insert", collection.FindById(41)?.Name);

                var inserted = new[]
                {
                    new PhaseCScalarRecord { Name = "batch-first", Score = 2 },
                    new PhaseCScalarRecord { Name = "batch-second", Score = 3 }
                };
                Assert.AreEqual(2, collection.Insert(inserted));
                Assert.AreNotEqual(0, inserted[0].Id);
                Assert.AreNotEqual(0, inserted[1].Id);
                Assert.AreNotEqual(inserted[0].Id, inserted[1].Id);

#pragma warning disable CS0618
                var bulkInserted = new[]
                {
                    new PhaseCScalarRecord { Name = "bulk-first", Score = 4 },
                    new PhaseCScalarRecord { Name = "bulk-second", Score = 5 }
                };
                Assert.AreEqual(2, collection.InsertBulk(bulkInserted, batchSize: 1));
#pragma warning restore CS0618
                Assert.AreNotEqual(0, bulkInserted[0].Id);
                Assert.AreNotEqual(0, bulkInserted[1].Id);

                inserted[0].Score = 20;
                inserted[1].Score = 30;
                Assert.AreEqual(2, collection.Update(inserted));
                Assert.AreEqual(20, collection.FindById(inserted[0].Id)?.Score);
                Assert.AreEqual(30, collection.FindById(inserted[1].Id)?.Score);

                var explicitUpdate = new PhaseCScalarRecord { Id = 999, Name = "explicit-update", Score = 40 };
                Assert.IsTrue(collection.Update(41, explicitUpdate));
                Assert.AreEqual(999, explicitUpdate.Id);
                var updated = collection.FindById(41);
                Assert.IsNotNull(updated);
                Assert.AreEqual("explicit-update", updated.Name);
                Assert.AreEqual(40, updated.Score);
            }
            finally
            {
                File.Delete(path);
            }
        }

        [TestMethod]
        public void GetGeneratedCollection_InsertBulk_HonorsBatchSizeAndValidatesItBeforeEnumeration()
        {
            var path = GetDatabasePath();

            try
            {
                var mapper = new ThrowingConversionMapper();
                LiteDbGeneratedMappings.Register(mapper);

                using var database = new LiteDatabase(path, mapper);
                var collection = database.GetGeneratedCollection<PhaseCScalarRecord>("phaseCScalarBulkBatches");
                var enumerationCount = 0;

#pragma warning disable CS0618
                var invalid = Assert.ThrowsException<ArgumentOutOfRangeException>(() =>
                    collection.InsertBulk(CountEnumeration(), batchSize: 0));
                Assert.AreEqual("batchSize", invalid.ParamName);
                Assert.AreEqual(0, enumerationCount);

                var records = Enumerable.Range(1, 5)
                    .Select(value => new PhaseCScalarRecord { Name = $"bulk-{value}", Score = value })
                    .ToArray();
                Assert.AreEqual(5, collection.InsertBulk(records, batchSize: 2));
#pragma warning restore CS0618

                Assert.AreEqual(5, collection.Count());
                Assert.IsTrue(records.All(record => record.Id != 0));

                IEnumerable<PhaseCScalarRecord> CountEnumeration()
                {
                    enumerationCount++;
                    yield return new PhaseCScalarRecord();
                }
            }
            finally
            {
                File.Delete(path);
            }
        }

        [TestMethod]
        public void GetGeneratedCollection_UnsupportedOperation_DescribesApprovedOverloads()
        {
            var path = GetDatabasePath();

            try
            {
                var mapper = new ThrowingConversionMapper();
                LiteDbGeneratedMappings.Register(mapper);

                using var database = new LiteDatabase(path, mapper);
                var collection = database.GetGeneratedCollection<PhaseCScalarRecord>("phaseCUnsupported");
                var exception = Assert.ThrowsException<NotSupportedException>(() => collection.FindAll());

                Assert.AreEqual(
                    "Generated collections support only Insert (single, explicit-ID, enumerable, and bulk), Update (single, explicit-ID, and enumerable), Upsert (single, explicit-ID, and enumerable), FindById, parameterless Count, and Delete. Operation 'FindAll' is not supported.",
                    exception.Message);
                Assert.AreEqual(0, database.GetCollection("phaseCUnsupported").Count());
            }
            finally
            {
                File.Delete(path);
            }
        }


        [TestMethod]
        public void GetGeneratedCollection_AutomaticC2ScalarMap_ExecutesUpsertsWithoutMapperFallback()
        {
            var path = GetDatabasePath();

            try
            {
                var mapper = new ThrowingConversionMapper();
                LiteDbGeneratedMappings.Register(mapper);

                using var database = new LiteDatabase(path, mapper);
                var collection = database.GetGeneratedCollection<PhaseCScalarRecord>("phaseCScalarUpserts");

                var automatic = new PhaseCScalarRecord { Name = "automatic", Score = 1 };
                Assert.IsTrue(collection.Upsert(automatic));
                Assert.AreNotEqual(0, automatic.Id);
                automatic.Name = "automatic-updated";
                automatic.Score = 2;
                Assert.IsFalse(collection.Upsert(automatic));
                Assert.AreEqual(1, collection.Count());
                Assert.AreEqual("automatic-updated", collection.FindById(automatic.Id)?.Name);
                Assert.AreEqual(2, collection.FindById(automatic.Id)?.Score);

                var batch = new[]
                {
                    new PhaseCScalarRecord { Name = "batch-first", Score = 3 },
                    new PhaseCScalarRecord { Name = "batch-second", Score = 4 }
                };
                Assert.AreEqual(2, collection.Upsert(batch));
                Assert.AreNotEqual(0, batch[0].Id);
                Assert.AreNotEqual(0, batch[1].Id);
                Assert.AreNotEqual(batch[0].Id, batch[1].Id);
                batch[0].Score = 30;
                batch[1].Score = 40;
                Assert.AreEqual(0, collection.Upsert(batch));
                Assert.AreEqual(30, collection.FindById(batch[0].Id)?.Score);
                Assert.AreEqual(40, collection.FindById(batch[1].Id)?.Score);

                var explicitEntity = new PhaseCScalarRecord { Id = 900, Name = "explicit", Score = 5 };
                Assert.IsTrue(collection.Upsert(41, explicitEntity));
                Assert.AreEqual(900, explicitEntity.Id);
                Assert.AreEqual("explicit", collection.FindById(41)?.Name);
                explicitEntity.Name = "explicit-updated";
                explicitEntity.Score = 50;
                Assert.IsFalse(collection.Upsert(41, explicitEntity));
                Assert.AreEqual(900, explicitEntity.Id);
                Assert.AreEqual("explicit-updated", collection.FindById(41)?.Name);
                Assert.AreEqual(50, collection.FindById(41)?.Score);
                Assert.AreEqual(4, collection.Count());
            }
            finally
            {
                File.Delete(path);
            }
        }

        private static void AssertCanonicalDateTimeOffsetValue(BsonValue value, DateTimeOffset expected)
        {
            Assert.IsTrue(value.IsDateTime);
            Assert.AreEqual(GetCanonicalDateTimeOffsetTicks(expected), value.AsDateTime.ToUniversalTime().Ticks);
        }

        private static void AssertCanonicalDateTimeOffset(DateTimeOffset expected, DateTimeOffset actual)
        {
            Assert.AreEqual(GetCanonicalDateTimeOffsetTicks(expected), actual.UtcTicks);
            Assert.AreEqual(TimeSpan.Zero, actual.Offset);
        }

        private static long GetCanonicalDateTimeOffsetTicks(DateTimeOffset value) =>
            value == DateTimeOffset.MinValue || value == DateTimeOffset.MaxValue
                ? DateTime.SpecifyKind(value.UtcDateTime, DateTimeKind.Unspecified).ToUniversalTime().Ticks
                : value.UtcTicks - (value.UtcTicks % TimeSpan.TicksPerMillisecond);

        private static BsonMapper CreateGeneratedMapper()
        {
            var mapper = new BsonMapper();
            LiteDbGeneratedMappings.Register(mapper);
            return mapper;
        }

        private static GeneratedEntityMap<PhaseBGeneratedRecord> CreatePhaseBExecutionMap()
        {
            return new GeneratedEntityMap<PhaseBGeneratedRecord>(
                record => new BsonDocument
                {
                    ["_id"] = record.Id,
                    [nameof(PhaseBGeneratedRecord.Name)] = record.Name,
                    [nameof(PhaseBGeneratedRecord.Score)] = record.Score
                },
                document => new PhaseBGeneratedRecord
                {
                    Id = document["_id"].AsInt32,
                    Name = document[nameof(PhaseBGeneratedRecord.Name)].AsString,
                    Score = document[nameof(PhaseBGeneratedRecord.Score)].AsInt64
                });
        }

        private static string GetDatabasePath()
        {
            return Path.Combine(Path.GetTempPath(), $"litedb-source-generated-test-{Guid.NewGuid():N}.db");
        }

        private sealed class RecordingEngine : ILiteEngine
        {
            public int DataAccessCount { get; private set; }

            private T Access<T>()
            {
                DataAccessCount++;
                throw new AssertFailedException("Generated configuration validation must run before engine access.");
            }

            public IBsonDataReader Query(string collection, Query query) => Access<IBsonDataReader>();
            public int Insert(string collection, IEnumerable<BsonDocument> docs, BsonAutoId autoId) => Access<int>();
            public int Update(string collection, IEnumerable<BsonDocument> docs) => Access<int>();
            public int UpdateMany(string collection, BsonExpression transform, BsonExpression predicate) => Access<int>();
            public int Upsert(string collection, IEnumerable<BsonDocument> docs, BsonAutoId autoId) => Access<int>();
            public int Delete(string collection, IEnumerable<BsonValue> ids) => Access<int>();
            public int DeleteMany(string collection, BsonExpression predicate) => Access<int>();
            public int Checkpoint() => Access<int>();
            public long Rebuild(RebuildOptions options) => Access<long>();
            public bool BeginTrans() => Access<bool>();
            public bool Commit() => Access<bool>();
            public bool Rollback() => Access<bool>();
            public bool DropCollection(string name) => Access<bool>();
            public bool RenameCollection(string name, string newName) => Access<bool>();
            public bool EnsureIndex(string collection, string name, BsonExpression expression, bool unique) => Access<bool>();
            public bool EnsureVectorIndex(string collection, string name, BsonExpression expression, LiteDB.Vector.VectorIndexOptions options) => Access<bool>();
            public bool DropIndex(string collection, string name) => Access<bool>();
            public BsonValue Pragma(string name) => Access<BsonValue>();
            public bool Pragma(string name, BsonValue value) => Access<bool>();
            public void Dispose()
            {
            }
        }
    }

    public sealed class UnsupportedExecutionRecord
    {
        public int Id { get; set; }
    }

    [BsonSourceGenerated]
    public sealed class PhaseCScalarRecord
    {
        public int Id { get; set; }

        public string? Name { get; set; }

        public int Score { get; set; }
    }

    public enum PhaseCScalarState
    {
        Ready = 1,
        Completed = 5
    }

    [BsonSourceGenerated]
    public sealed class PhaseCScalarCompatibilityRecord
    {
        public int Id { get; set; }
        public bool BooleanValue { get; set; }
        public byte ByteValue { get; set; }
        public sbyte SignedByteValue { get; set; }
        public char Character { get; set; }
        public short SignedShort { get; set; }
        public ushort UnsignedShort { get; set; }
        public int SignedInteger { get; set; }
        public uint UnsignedInteger { get; set; }
        public long SignedLong { get; set; }
        public ulong UnsignedLong { get; set; }
        public float SingleValue { get; set; }
        public double DoubleValue { get; set; }
        public decimal DecimalValue { get; set; }
        public PhaseCScalarState State { get; set; }
        public DateTime Timestamp { get; set; }
        public ObjectId ObjectId { get; set; } = LiteDB.ObjectId.Empty;
        public Guid CorrelationId { get; set; }
        public byte[] Payload { get; set; } = [];
        public string? Name { get; set; }
        public int? NullableInteger { get; set; }
        public PhaseCScalarState? NullableState { get; set; }
        public ObjectId? NullableObjectId { get; set; }
        public Guid? NullableCorrelationId { get; set; }
        public DateTime? NullableTimestamp { get; set; }
        public byte[]? NullablePayload { get; set; }
    }

    [BsonSourceGenerated]
    public sealed class PhaseCAttributedScalarRecord
    {
        public int Id { get; set; }

        [BsonField("score")]
        public int Score { get; set; }
    }

    [BsonSourceGenerated]
    public sealed class PhaseBGeneratedRecord : PhaseBGeneratedRecordBase
    {
        public int Id { get; set; }

        public string Name { get; set; } = string.Empty;

        public long Score { get; set; }

        public Dictionary<string, object?> LegacyProbe { get; set; } = [];
    }

    // Keeps the retained manual Phase B registry fixture outside automatic direct-map emission.
    public class PhaseBGeneratedRecordBase
    {
    }

    internal sealed class ThrowingConversionMapper : BsonMapper
    {
        public override BsonDocument ToDocument(Type type, object entity) =>
            throw new AssertFailedException($"Generated execution must not call {nameof(ToDocument)}.");

        public override object ToObject(Type type, BsonDocument document) =>
            throw new AssertFailedException($"Generated execution must not call {nameof(ToObject)}.");
    }

    [BsonSourceGenerated]
    public sealed record MutableGeneratedRecord
    {
        public int Id { get; set; }

        public string Name { get; set; } = string.Empty;
    }

    public class OverrideRecordBase
    {
        [BsonId(false)]
        public virtual int OverrideId { get; set; }

        [BsonField("stored_name")]
        public virtual string Name { get; set; } = string.Empty;

        [BsonIgnore]
        public virtual string? Ignored { get; set; }
    }

    public class OverrideRecordMiddle : OverrideRecordBase
    {
        public override int OverrideId { get; set; }
        public override string Name { get; set; } = string.Empty;
        public override string? Ignored { get; set; }
    }

    [BsonSourceGenerated]
    public sealed class OverrideRecord : OverrideRecordMiddle
    {
        private int _overrideId;

        public override int OverrideId
        {
            get => _overrideId;
            set
            {
                _overrideId = value;
                SetterCalls++;
            }
        }

        public override string Name { get; set; } = string.Empty;
        public override string? Ignored { get; set; }

        [BsonIgnore]
        public int SetterCalls { get; private set; }
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

    internal sealed class UnsupportedDynamicDictionaryValue;

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
        public DateTimeOffset Minimum { get; set; }
        public DateTimeOffset Maximum { get; set; }
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
