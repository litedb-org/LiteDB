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
    public sealed class SourceGeneratedMappingScalarTests
    {
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
                Assert.AreEqual(1, collection.FindAll().Count());
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
    }
}
