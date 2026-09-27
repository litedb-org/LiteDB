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
    public sealed class SourceGeneratedMappingCollectionTests
    {
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
        public void GetGeneratedCollection_GeneratedScalarMap_RoundTripsSupportedScalarMatrixWithoutMapperFallback()
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
                var collection = database.GetGeneratedCollection<ScalarCompatibilityRecord>("generatedScalarCompatibility");
                collection.Insert(new ScalarCompatibilityRecord
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
                    State = GeneratedScalarState.Completed,
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

                var document = database.GetCollection("generatedScalarCompatibility").FindById(1);
                Assert.AreEqual(BsonType.Boolean, document[nameof(ScalarCompatibilityRecord.BooleanValue)].Type);
                Assert.AreEqual(BsonType.Int32, document[nameof(ScalarCompatibilityRecord.ByteValue)].Type);
                Assert.AreEqual(BsonType.Int32, document[nameof(ScalarCompatibilityRecord.SignedByteValue)].Type);
                Assert.AreEqual(BsonType.String, document[nameof(ScalarCompatibilityRecord.Character)].Type);
                Assert.AreEqual(BsonType.Int32, document[nameof(ScalarCompatibilityRecord.SignedShort)].Type);
                Assert.AreEqual(BsonType.Int32, document[nameof(ScalarCompatibilityRecord.UnsignedShort)].Type);
                Assert.AreEqual(BsonType.Int32, document[nameof(ScalarCompatibilityRecord.SignedInteger)].Type);
                Assert.AreEqual(BsonType.Int64, document[nameof(ScalarCompatibilityRecord.UnsignedInteger)].Type);
                Assert.AreEqual(BsonType.Int64, document[nameof(ScalarCompatibilityRecord.SignedLong)].Type);
                Assert.AreEqual(BsonType.Int64, document[nameof(ScalarCompatibilityRecord.UnsignedLong)].Type);
                Assert.AreEqual(BsonType.Double, document[nameof(ScalarCompatibilityRecord.SingleValue)].Type);
                Assert.AreEqual(BsonType.Double, document[nameof(ScalarCompatibilityRecord.DoubleValue)].Type);
                Assert.AreEqual(BsonType.Decimal, document[nameof(ScalarCompatibilityRecord.DecimalValue)].Type);
                Assert.AreEqual(BsonType.String, document[nameof(ScalarCompatibilityRecord.State)].Type);
                Assert.AreEqual("Completed", document[nameof(ScalarCompatibilityRecord.State)].AsString);
                Assert.AreEqual(BsonType.DateTime, document[nameof(ScalarCompatibilityRecord.Timestamp)].Type);
                Assert.AreEqual(BsonType.ObjectId, document[nameof(ScalarCompatibilityRecord.ObjectId)].Type);
                Assert.AreEqual(BsonType.Guid, document[nameof(ScalarCompatibilityRecord.CorrelationId)].Type);
                Assert.AreEqual(BsonType.Binary, document[nameof(ScalarCompatibilityRecord.Payload)].Type);
                Assert.AreEqual("scalar compatibility", document[nameof(ScalarCompatibilityRecord.Name)].AsString);
                Assert.IsTrue(document[nameof(ScalarCompatibilityRecord.NullableInteger)].IsNull);
                Assert.IsTrue(document[nameof(ScalarCompatibilityRecord.NullableState)].IsNull);
                Assert.IsTrue(document[nameof(ScalarCompatibilityRecord.NullableObjectId)].IsNull);
                Assert.IsTrue(document[nameof(ScalarCompatibilityRecord.NullableCorrelationId)].IsNull);
                Assert.IsTrue(document[nameof(ScalarCompatibilityRecord.NullableTimestamp)].IsNull);
                Assert.IsTrue(document[nameof(ScalarCompatibilityRecord.NullablePayload)].IsNull);

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
                Assert.AreEqual(GeneratedScalarState.Completed, actual.State);
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
                collection.Insert(new ScalarCompatibilityRecord { Id = 2, State = GeneratedScalarState.Ready });
                var integerEnumDocument = database.GetCollection("generatedScalarCompatibility").FindById(2);
                Assert.AreEqual(BsonType.Int32, integerEnumDocument[nameof(ScalarCompatibilityRecord.State)].Type);
                Assert.AreEqual((int)GeneratedScalarState.Ready, integerEnumDocument[nameof(ScalarCompatibilityRecord.State)].AsInt32);
                Assert.AreEqual(GeneratedScalarState.Ready, collection.FindById(2)?.State);
            }
            finally
            {
                File.Delete(path);
            }
        }

        [TestMethod]
        public void GetGeneratedCollection_GeneratedScalarMap_CrossReadsWithOrdinaryCollection()
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
                    database.GetCollection<ScalarCompatibilityRecord>("generatedScalarCrossRead").Insert(new ScalarCompatibilityRecord
                    {
                        Id = 1,
                        Name = "  ordinary writer  ",
                        UnsignedInteger = uint.MaxValue,
                        State = GeneratedScalarState.Completed,
                        NullableState = GeneratedScalarState.Ready,
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
                    var direct = database.GetGeneratedCollection<ScalarCompatibilityRecord>("generatedScalarCrossRead");
                    var ordinaryWritten = direct.FindById(1);
                    Assert.IsNotNull(ordinaryWritten);
                    Assert.AreEqual("ordinary writer", ordinaryWritten.Name);
                    Assert.AreEqual(uint.MaxValue, ordinaryWritten.UnsignedInteger);
                    Assert.AreEqual(GeneratedScalarState.Completed, ordinaryWritten.State);
                    Assert.AreEqual(GeneratedScalarState.Ready, ordinaryWritten.NullableState);
                    Assert.AreEqual(expectedObjectId, ordinaryWritten.ObjectId);
                    Assert.AreEqual(expectedObjectId, ordinaryWritten.NullableObjectId);
                    Assert.AreEqual(expectedCorrelationId, ordinaryWritten.CorrelationId);
                    Assert.AreEqual(expectedCorrelationId, ordinaryWritten.NullableCorrelationId);
                    Assert.AreEqual(ordinaryTimestamp, ordinaryWritten.Timestamp.ToUniversalTime());
                    Assert.IsTrue(ordinaryWritten.NullableTimestamp.HasValue);
                    Assert.AreEqual(ordinaryNullableTimestamp, ordinaryWritten.NullableTimestamp.Value.ToUniversalTime());
                    CollectionAssert.AreEqual(new byte[] { 1, 2, 3 }, ordinaryWritten.Payload);
                    CollectionAssert.AreEqual(new byte[] { 4, 5 }, ordinaryWritten.NullablePayload);

                    direct.Insert(new ScalarCompatibilityRecord
                    {
                        Id = 2,
                        Name = "  direct writer  ",
                        UnsignedInteger = uint.MaxValue - 1,
                        State = GeneratedScalarState.Ready,
                        NullableState = GeneratedScalarState.Completed,
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
                var directWritten = readerDatabase.GetCollection<ScalarCompatibilityRecord>("generatedScalarCrossRead").FindById(2);
                Assert.IsNotNull(directWritten);
                Assert.AreEqual("direct writer", directWritten.Name);
                Assert.AreEqual(uint.MaxValue - 1, directWritten.UnsignedInteger);
                Assert.AreEqual(GeneratedScalarState.Ready, directWritten.State);
                Assert.AreEqual(GeneratedScalarState.Completed, directWritten.NullableState);
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
    }
}
