using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

using LiteDB.Generated;

#nullable enable
namespace LiteDB.AotSmokeTests
{
    internal static partial class Program
    {
        private static void RunGeneratedBoundaryScenarios(LiteDatabase database)
        {
            Console.WriteLine("  [3.11] Round-trip DateTimeOffset offset directions and canonical raw BSON DateTime values.");
            var expectedPositiveOffset = new DateTimeOffset(2024, 7, 8, 9, 10, 11, TimeSpan.FromHours(5.5)).AddTicks(1234);
            var expectedNegativeOffset = new DateTimeOffset(2024, 7, 9, 10, 11, 12, TimeSpan.FromHours(-8)).AddTicks(4321);
            var expectedNullableOffset = new DateTimeOffset(2024, 7, 10, 11, 12, 13, TimeSpan.Zero).AddTicks(9876);
            var dateTimeOffsetBoundaries = database.GetGeneratedCollection<AotDateTimeOffsetBoundaryRecord>("aot_date_time_offset_boundaries");
            dateTimeOffsetBoundaries.Insert(new AotDateTimeOffsetBoundaryRecord
            {
                Id = 80,
                PositiveOffset = expectedPositiveOffset,
                NegativeOffset = expectedNegativeOffset,
                NullableOffset = expectedNullableOffset
            });
            dateTimeOffsetBoundaries.Insert(new AotDateTimeOffsetBoundaryRecord
            {
                Id = 81,
                PositiveOffset = expectedPositiveOffset,
                NegativeOffset = expectedNegativeOffset,
                NullableOffset = null
            });

            var dateTimeOffsetBoundaryRead = dateTimeOffsetBoundaries.FindById(80);
            var nullDateTimeOffsetBoundaryRead = dateTimeOffsetBoundaries.FindById(81);
            var dateTimeOffsetBoundaryDocument = database.GetCollection("aot_date_time_offset_boundaries").FindById(80);
            Require(dateTimeOffsetBoundaryRead is not null &&
                    IsCanonicalDateTimeOffset(expectedPositiveOffset, dateTimeOffsetBoundaryRead.PositiveOffset) &&
                    IsCanonicalDateTimeOffset(expectedNegativeOffset, dateTimeOffsetBoundaryRead.NegativeOffset) &&
                    dateTimeOffsetBoundaryRead.NullableOffset.HasValue &&
                    IsCanonicalDateTimeOffset(expectedNullableOffset, dateTimeOffsetBoundaryRead.NullableOffset.Value),
                "The source-generated Native AOT DateTimeOffset boundary round trip failed.");
            Require(nullDateTimeOffsetBoundaryRead is not null && nullDateTimeOffsetBoundaryRead.NullableOffset is null,
                "The source-generated Native AOT nullable DateTimeOffset BSON-null round trip failed.");
            RequireCanonicalDateTimeOffsetValue(dateTimeOffsetBoundaryDocument[nameof(AotDateTimeOffsetBoundaryRecord.PositiveOffset)], expectedPositiveOffset);
            RequireCanonicalDateTimeOffsetValue(dateTimeOffsetBoundaryDocument[nameof(AotDateTimeOffsetBoundaryRecord.NegativeOffset)], expectedNegativeOffset);
            RequireCanonicalDateTimeOffsetValue(dateTimeOffsetBoundaryDocument[nameof(AotDateTimeOffsetBoundaryRecord.NullableOffset)], expectedNullableOffset);
            Console.WriteLine("        Passed: positive, negative, and nullable offsets canonicalize to UTC BSON DateTime values.");

            Console.WriteLine("  [3.12] Round-trip additional populated and BSON-null nullable scalar boundaries.");
            var expectedNullableOffsetScalar = new DateTimeOffset(2024, 7, 11, 12, 13, 14, TimeSpan.FromHours(-3)).AddTicks(5678);
            var nullableScalarBoundaries = database.GetGeneratedCollection<AotNullableScalarBoundaryRecord>("aot_nullable_scalar_boundaries");
            nullableScalarBoundaries.Insert(new AotNullableScalarBoundaryRecord
            {
                Id = 90,
                SignedShort = -12_345,
                UnsignedLong = 9_000_000_000_000_000_000UL,
                Ratio = 123.5d,
                Amount = 456.789m,
                TimestampWithOffset = expectedNullableOffsetScalar
            });
            nullableScalarBoundaries.Insert(new AotNullableScalarBoundaryRecord { Id = 91 });

            var populatedNullableScalarBoundaries = nullableScalarBoundaries.FindById(90);
            var nullNullableScalarBoundaries = nullableScalarBoundaries.FindById(91);
            var nullNullableScalarBoundaryDocument = database.GetCollection("aot_nullable_scalar_boundaries").FindById(91);
            Require(populatedNullableScalarBoundaries is not null &&
                    populatedNullableScalarBoundaries.SignedShort == -12_345 &&
                    populatedNullableScalarBoundaries.UnsignedLong == 9_000_000_000_000_000_000UL &&
                    populatedNullableScalarBoundaries.Ratio == 123.5d &&
                    populatedNullableScalarBoundaries.Amount == 456.789m &&
                    populatedNullableScalarBoundaries.TimestampWithOffset.HasValue &&
                    IsCanonicalDateTimeOffset(expectedNullableOffsetScalar, populatedNullableScalarBoundaries.TimestampWithOffset.Value),
                "The source-generated Native AOT populated nullable-scalar boundary round trip failed.");
            Require(nullNullableScalarBoundaries is not null &&
                    nullNullableScalarBoundaries.SignedShort is null &&
                    nullNullableScalarBoundaries.UnsignedLong is null &&
                    nullNullableScalarBoundaries.Ratio is null &&
                    nullNullableScalarBoundaries.Amount is null &&
                    nullNullableScalarBoundaries.TimestampWithOffset is null &&
                    nullNullableScalarBoundaryDocument[nameof(AotNullableScalarBoundaryRecord.SignedShort)].IsNull &&
                    nullNullableScalarBoundaryDocument[nameof(AotNullableScalarBoundaryRecord.UnsignedLong)].IsNull &&
                    nullNullableScalarBoundaryDocument[nameof(AotNullableScalarBoundaryRecord.Ratio)].IsNull &&
                    nullNullableScalarBoundaryDocument[nameof(AotNullableScalarBoundaryRecord.Amount)].IsNull &&
                    nullNullableScalarBoundaryDocument[nameof(AotNullableScalarBoundaryRecord.TimestampWithOffset)].IsNull,
                "The source-generated Native AOT BSON-null nullable-scalar boundary round trip failed.");
            Console.WriteLine("        Passed: nullable short, ulong, double, decimal, and DateTimeOffset values and BSON nulls.");

            Console.WriteLine("  [3.13] Round-trip three-level inheritance and recompute multiple projections.");
            var multiLevelInherited = database.GetGeneratedCollection<AotMultiLevelInheritedRecord>("aot_multi_level_inherited");
            multiLevelInherited.Insert(new AotMultiLevelInheritedRecord
            {
                RootId = 100,
                Origin = "grandparent",
                ParentName = "parent",
                IgnoredParentValue = "not persisted",
                Values = ["content", "acl", "streams"],
                DerivedName = "derived"
            });

            var multiLevelInheritedRead = multiLevelInherited.FindById(100);
            var multiLevelInheritedDocument = database.GetCollection("aot_multi_level_inherited").FindById(100);
            Require(multiLevelInheritedRead is not null &&
                    multiLevelInheritedRead.RootId == 100 &&
                    multiLevelInheritedRead.Origin == "grandparent" &&
                    multiLevelInheritedRead.ParentName == "parent" &&
                    multiLevelInheritedRead.IgnoredParentValue is null &&
                    multiLevelInheritedRead.Values.SequenceEqual(["content", "acl", "streams"]) &&
                    multiLevelInheritedRead.DerivedName == "derived" &&
                    multiLevelInheritedRead.Fingerprint == "grandparent|derived|content,acl,streams" &&
                    multiLevelInheritedRead.ValueCount == 3 &&
                    !multiLevelInheritedDocument.ContainsKey(nameof(AotMultiLevelInheritedRecord.IgnoredParentValue)) &&
                    !multiLevelInheritedDocument.ContainsKey(nameof(AotMultiLevelInheritedRecord.Fingerprint)) &&
                    !multiLevelInheritedDocument.ContainsKey(nameof(AotMultiLevelInheritedRecord.ValueCount)),
                "The source-generated Native AOT multi-level inheritance and computed-projection round trip failed.");
            Console.WriteLine("        Passed: inherited ID, named field, ignored member, list, derived value, and multiple computed projections.");

            Console.WriteLine("  [3.14] Round-trip string-array element boundaries and preserve order.");
            var expectedStringArrayBoundary = Enumerable.Range(0, 64)
                .Select(index => index == 0 ? string.Empty : index == 63 ? "last" : $"value-{index:D2}")
                .ToArray();
            var normalizedStringArrayBoundary = expectedStringArrayBoundary.ToArray() as string?[];
            normalizedStringArrayBoundary[0] = null;
            var stringArrayBoundaries = database.GetGeneratedCollection<AotStringArrayRecord>("aot_string_array_boundaries");
            stringArrayBoundaries.Insert(new AotStringArrayRecord { Id = 110, StreamNames = [string.Empty] });
            stringArrayBoundaries.Insert(new AotStringArrayRecord { Id = 111, StreamNames = expectedStringArrayBoundary });

            var singleStringArrayBoundary = stringArrayBoundaries.FindById(110);
            var manyStringArrayBoundary = stringArrayBoundaries.FindById(111);
            var manyStringArrayBoundaryDocument = database.GetCollection("aot_string_array_boundaries").FindById(111);
            Require(singleStringArrayBoundary is not null &&
                    singleStringArrayBoundary.StreamNames is not null &&
                    singleStringArrayBoundary.StreamNames.Length == 1 &&
                    singleStringArrayBoundary.StreamNames[0] is null &&
                    manyStringArrayBoundary is not null &&
                    manyStringArrayBoundary.StreamNames is not null &&
                    manyStringArrayBoundary.StreamNames.SequenceEqual(normalizedStringArrayBoundary) &&
                    manyStringArrayBoundaryDocument[nameof(AotStringArrayRecord.StreamNames)].AsArray.Count == expectedStringArrayBoundary.Length &&
                    manyStringArrayBoundaryDocument[nameof(AotStringArrayRecord.StreamNames)].AsArray[0].IsNull &&
                    manyStringArrayBoundaryDocument[nameof(AotStringArrayRecord.StreamNames)].AsArray[63].AsString == "last",
                "The source-generated Native AOT string-array boundary normalization round trip failed.");
            Console.WriteLine("        Passed: empty-string normalization, bounded array length, BSON array shape, and element order.");

            Console.WriteLine("  [3.15] Round-trip recursive dynamic dictionaries and reject unsupported values.");
            var dynamicDictionaryBoundaries = database.GetGeneratedCollection<AotDynamicDictionaryRecord>("aot_dynamic_dictionary_boundaries");
            dynamicDictionaryBoundaries.Insert(new AotDynamicDictionaryRecord { Id = 120, Fields = [] });
            dynamicDictionaryBoundaries.Insert(new AotDynamicDictionaryRecord
            {
                Id = 121,
                Fields = new Dictionary<string, object?>
                {
                    ["int32"] = 12,
                    ["int64"] = 9_000_000_000L,
                    ["double"] = 3.5d,
                    ["decimal"] = 6.75m,
                    ["rawDocument"] = new BsonDocument { ["kind"] = "raw" },
                    ["rawArray"] = new BsonArray { "first", 2 },
                    ["nestedArray"] = new object[]
                    {
                        new Dictionary<string, object> { ["inner"] = "value" },
                        new object[] { "nested", 4 }
                    }
                }
            });

            var emptyDynamicDictionary = dynamicDictionaryBoundaries.FindById(120);
            var populatedDynamicDictionary = dynamicDictionaryBoundaries.FindById(121);
            var dynamicDictionaryBoundaryDocument = database.GetCollection("aot_dynamic_dictionary_boundaries").FindById(121);
            var dynamicFields = dynamicDictionaryBoundaryDocument[nameof(AotDynamicDictionaryRecord.Fields)].AsDocument;
            Require(emptyDynamicDictionary is not null && emptyDynamicDictionary.Fields is not null && emptyDynamicDictionary.Fields.Count == 0,
                "The source-generated Native AOT empty dynamic dictionary round trip failed.");
            Require(populatedDynamicDictionary is not null &&
                    (int)populatedDynamicDictionary.Fields["int32"] == 12 &&
                    (long)populatedDynamicDictionary.Fields["int64"] == 9_000_000_000L &&
                    (double)populatedDynamicDictionary.Fields["double"] == 3.5d &&
                    (decimal)populatedDynamicDictionary.Fields["decimal"] == 6.75m &&
                    (((Dictionary<string, object>?)populatedDynamicDictionary.Fields["rawDocument"])["kind"] as string) == "raw" &&
                    (((object[]?)populatedDynamicDictionary.Fields["rawArray"])[0] as string) == "first" &&
                    (int)((object[]?)populatedDynamicDictionary.Fields["rawArray"])[1] == 2 &&
                    (((Dictionary<string, object>)((object[]?)populatedDynamicDictionary.Fields["nestedArray"])[0])["inner"] as string) == "value" &&
                    (((object[])((object[]?)populatedDynamicDictionary.Fields["nestedArray"])[1])[0] as string) == "nested" &&
                    (int)((object[])((object[]?)populatedDynamicDictionary.Fields["nestedArray"])[1])[1] == 4 &&
                    dynamicFields["int32"].Type == BsonType.Int32 &&
                    dynamicFields["int64"].Type == BsonType.Int64 &&
                    dynamicFields["double"].Type == BsonType.Double &&
                    dynamicFields["decimal"].Type == BsonType.Decimal &&
                    dynamicFields["rawDocument"].AsDocument["kind"].AsString == "raw" &&
                    dynamicFields["rawArray"].AsArray.Count == 2,
                "The source-generated Native AOT dynamic dictionary boundary round trip failed.");
            RequireThrows<InvalidOperationException>(() => dynamicDictionaryBoundaries.Insert(new AotDynamicDictionaryRecord
            {
                Id = 122,
                Fields = new Dictionary<string, object> { ["unsupported"] = new UnsupportedAotDynamicDictionaryValue() }
            }), "The source-generated Native AOT dynamic dictionary accepted an arbitrary POCO.");
            RequireThrows<InvalidOperationException>(() => dynamicDictionaryBoundaries.Insert(new AotDynamicDictionaryRecord
            {
                Id = 123,
                Fields = new Dictionary<string, object?>
                {
                    ["unsupported"] = new DateTimeOffset(2024, 7, 12, 13, 14, 15, TimeSpan.Zero)
                }
            }), "The source-generated Native AOT dynamic dictionary accepted DateTimeOffset without a schema.");
            Console.WriteLine("        Passed: empty, numeric, raw BSON, recursive dynamic values, and unsupported-value rejection.");
        }
    }
}
