using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

using LiteDB.Engine;
using LiteDB.Generated;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using static LiteDB.AotTests.SourceGeneratedMappingTestHelper;

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
                var collection = database.GetGeneratedCollection<ManualGeneratedRecord>("manual");
                var record = new ManualGeneratedRecord { Name = "inserted", Score = 7L };

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

                Assert.AreEqual(0, collection.FindAll().Count());

                var ordinaryCollection = database.GetCollection<ManualGeneratedRecord>("ordinaryManual");
                Assert.ThrowsException<AssertFailedException>(() =>
                    ordinaryCollection.Insert(new ManualGeneratedRecord { Name = "legacy", Score = 1L }));
            }
            finally
            {
                File.Delete(path);
            }
        }

        [TestMethod]
        public void GetGeneratedCollection_AutomaticallyRegistersScalarExecutionMap()
        {
            var path = GetDatabasePath();

            try
            {
                var mapper = new ThrowingConversionMapper();
                LiteDbGeneratedMappings.Register(mapper);

                using var database = new LiteDatabase(path, mapper);
                var collection = database.GetGeneratedCollection<GeneratedScalarRecord>("generatedScalar");
                var record = new GeneratedScalarRecord { Score = 7 };

                var id = collection.Insert(record);
                var document = database.GetCollection("generatedScalar").FindById(id);
                Assert.AreEqual(1, id.AsInt32);
                Assert.AreEqual(1, record.Id);
                Assert.AreEqual(1, document["_id"].AsInt32);
                Assert.AreEqual(7, document[nameof(GeneratedScalarRecord.Score)].AsInt32);
                Assert.IsFalse(document.ContainsKey(nameof(GeneratedScalarRecord.Name)));

                record.Name = "updated";
                Assert.IsTrue(collection.Update(record));
                var read = collection.FindById(id);
                Assert.IsNotNull(read);
                Assert.AreEqual("updated", read.Name);
                Assert.AreEqual(7, read.Score);
                Assert.AreEqual(1, collection.Count());
                Assert.IsTrue(collection.Delete(id));
                Assert.AreEqual(0, collection.Count());
                Assert.AreEqual(0, collection.FindAll().Count());
            }
            finally
            {
                File.Delete(path);
            }
        }

        [TestMethod]
        public void GetGeneratedCollection_GeneratedScalarMap_AcceptsSerializeNullValuesChangedAfterAcquisition()
        {
            var path = GetDatabasePath();

            try
            {
                var mapper = new BsonMapper();
                LiteDbGeneratedMappings.Register(mapper);

                using var database = new LiteDatabase(path, mapper);
                var collection = database.GetGeneratedCollection<GeneratedScalarRecord>("generatedNulls");
                mapper.SerializeNullValues = true;
                var id = collection.Insert(new GeneratedScalarRecord { Score = 8 });
                var document = database.GetCollection("generatedNulls").FindById(id);

                Assert.IsTrue(document.ContainsKey(nameof(GeneratedScalarRecord.Name)));
                Assert.IsTrue(document[nameof(GeneratedScalarRecord.Name)].IsNull);
                Assert.IsNull(collection.FindById(id)?.Name);
            }
            finally
            {
                File.Delete(path);
            }
        }

        [TestMethod]
        public void GetGeneratedCollection_GeneratedScalarMap_AppliesTrimWhitespaceAndEmptyStringToNullWithoutMapperFallback()
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
                var collection = database.GetGeneratedCollection<GeneratedScalarRecord>("generatedTrimmedString");
                var id = collection.Insert(new GeneratedScalarRecord { Name = "  ", Score = 8 });
                var document = database.GetCollection("generatedTrimmedString").FindById(id);

                Assert.IsTrue(document[nameof(GeneratedScalarRecord.Name)].IsNull);
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
                var collection = database.GetGeneratedCollection<ManualGeneratedRecord>("manualBridge");
                var id = collection.Insert(new ManualGeneratedRecord { Name = "generated", Score = 1L });
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
        public void GetGeneratedCollection_AutomaticallyRegistersDirectMapForAttributedModel()
        {
            var path = GetDatabasePath();

            try
            {
                var mapper = new ThrowingConversionMapper();
                LiteDbGeneratedMappings.Register(mapper);

                using var database = new LiteDatabase(path, mapper);
                var collection = database.GetGeneratedCollection<AttributedScalarRecord>("generatedAttributes");
                var record = new AttributedScalarRecord { Id = 1, Score = 8 };

                collection.Insert(record);

                var document = database.GetCollection("generatedAttributes").FindById(1);
                var read = collection.FindById(1);
                Assert.AreEqual(1, document["_id"].AsInt32);
                Assert.AreEqual(8, document["score"].AsInt32);
                Assert.IsNotNull(read);
                Assert.AreEqual(1, read.Id);
                Assert.AreEqual(8, read.Score);
                Assert.AreEqual(2, document.Count);
                Assert.AreEqual(1, collection.FindAll().Count());
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
                firstMapper.RegisterGeneratedExecutionMap(CreateManualExecutionMap()));

            var path = GetDatabasePath();
            try
            {
                using (var firstDatabase = new LiteDatabase(path, firstMapper))
                {
                    var generated = firstDatabase.GetGeneratedCollection<ManualGeneratedRecord>("manual");
                    Assert.AreEqual(0, generated.FindAll().Count());
                }

                using var secondDatabase = new LiteDatabase(path, secondMapper);
                var secondGenerated = secondDatabase.GetGeneratedCollection<ManualGeneratedRecord>("manual");
                Assert.AreEqual(0, secondGenerated.FindAll().Count());
            }
            finally
            {
                File.Delete(path);
            }
        }

        [TestMethod]
        public void GeneratedRegistration_DoesNotReplaceOrdinaryMapperMetadata()
        {
            var mapper = new BsonMapper
            {
                ResolveFieldName = static name => "ordinary_" + name
            };
            LiteDbGeneratedMappings.Register(mapper);
            var path = GetDatabasePath();

            try
            {
                using var database = new LiteDatabase(path, mapper);
                var ordinary = database.GetCollection<GeneratedScalarRecord>("ordinaryMetadata");
                ordinary.Insert(new GeneratedScalarRecord { Id = 7, Score = 11 });

                var document = database.GetCollection("ordinaryMetadata").FindById(7);
                Assert.IsTrue(document.ContainsKey("ordinary_Score"));
                Assert.IsFalse(document.ContainsKey(nameof(GeneratedScalarRecord.Score)));

                Assert.ThrowsException<InvalidOperationException>(() =>
                    database.GetGeneratedCollection<GeneratedScalarRecord>("generatedMetadata"));
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
                unregisteredMapper.RegisterGeneratedExecutionMap(CreateManualExecutionMap()));

            var mapper = new BsonMapper { SerializeNullValues = true };
            LiteDbGeneratedMappings.Register(mapper);

            var path = GetDatabasePath();
            try
            {
                using var database = new LiteDatabase(path, mapper);
                var collection = database.GetGeneratedCollection<ManualGeneratedRecord>("manual");
                var id = collection.Insert(new ManualGeneratedRecord { Name = null!, Score = 1L });
                var document = database.GetCollection("manual").FindById(id);

                Assert.IsTrue(document[nameof(ManualGeneratedRecord.Name)].IsNull);
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
                    database.GetGeneratedCollection<ManualGeneratedRecord>("manual"));

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
                var collection = database.GetGeneratedCollection<ManualGeneratedRecord>("manual");

                mapper.SerializeNullValues = true;

                Assert.AreEqual(0, collection.Count());
            }
            finally
            {
                File.Delete(path);
            }
        }
    }
}
