using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

using LiteDB.Generated;

namespace LiteDB.AotSmokeTests
{
    internal static class Program
    {
        private static void Main(string[] args)
        {
            var databasePath = Path.Combine(Path.GetTempPath(), $"litedb-aot-{Guid.NewGuid():N}.db");

            Console.WriteLine("LiteDB Native AOT smoke test");
            Console.WriteLine("This executable validates core document operations, stream-backed storage, and source-generated typed mappings.");
            Console.WriteLine("Each scenario reports its completed checks. Any failed requirement stops the program and prints its reason.");
            Console.WriteLine();

            try
            {
                RunScenario("1/3 Document and expression operations", () =>
                {
                    using var database = new LiteDatabase(databasePath);
                    RunDocumentAndExpressionScenarios(database);
                });
                RunScenario("2/3 Stream-backed database round trip", RunStreamBackedScenario);
                RunScenario("3/3 Source-generated typed mappings", () => RunGeneratedTypedMappingScenario(databasePath));

                Console.WriteLine("[RESULT] All Native AOT smoke scenarios passed.");
                Console.WriteLine("The executable successfully exercised LiteDB persistence, querying, stream storage, and generated typed mappings.");
            }
            finally
            {
                File.Delete(databasePath);
            }
        }

        private static void RunDocumentAndExpressionScenarios(LiteDatabase database)
        {
            var collection = database.GetCollection("items");

            Console.WriteLine("  [1.1] Insert BSON documents and verify a persisted document round trip.");
            collection.Insert(new BsonDocument
            {
                ["_id"] = 1,
                ["name"] = "native-aot",
                ["score"] = 2,
                ["values"] = new BsonArray { 1, 2, 3 }
            });
            collection.Insert(new BsonDocument
            {
                ["_id"] = 2,
                ["name"] = "secondary",
                ["score"] = 4,
                ["values"] = new BsonArray { 4, 5, 6 }
            });

            var item = collection.FindById(1);
            Require(item?["name"].AsString == "native-aot", "The Native AOT database round trip failed.");
            Console.WriteLine("        Passed: BSON document persistence and readback.");

            Console.WriteLine("  [1.2] Create an index and run parameterized and array expression queries.");
            Require(collection.EnsureIndex("score", BsonExpression.Create("$.score")), "The Native AOT index creation failed.");

            var between = collection.Query()
                .Where("$.score BETWEEN @0 AND @1", 1, 3)
                .ToArray();
            Require(between.Length == 1 && between[0]["_id"].AsInt32 == 1, "The Native AOT BETWEEN query failed.");

            var arrayMembership = collection.Query()
                .Where("2 IN [1, 2, 3]")
                .ToArray();
            Require(arrayMembership.Length == 2, "The Native AOT array expression query failed.");
            Console.WriteLine("        Passed: index creation, BETWEEN query, and array expression query.");

            Console.WriteLine("  [1.3] Execute SQL projection and update statements.");
            using (var projectionReader = database.Execute("SELECT name AS label, score + 1 AS next FROM items WHERE score BETWEEN 1 AND 3"))
            {
                var projections = projectionReader.ToArray();
                Require(projections.Length == 1 &&
                        projections[0].AsDocument["label"].AsString == "native-aot" &&
                        projections[0].AsDocument["next"].AsInt32 == 3,
                    "The Native AOT SELECT document builder failed.");
            }

            using (database.Execute("UPDATE items SET name = UPPER($.name), score = $.score + 10 WHERE _id = 1"))
            {
            }

            var updated = collection.FindById(1);
            Require(updated?["name"].AsString == "NATIVE-AOT" &&
                    updated["score"].AsInt32 == 12,
                "The Native AOT UPDATE document builder failed.");
            Console.WriteLine("        Passed: SQL projection and update.");

            Console.WriteLine("  [1.4] Upsert and delete a BSON document.");
            Require(collection.Upsert(new BsonDocument
            {
                ["_id"] = 3,
                ["name"] = "upserted",
                ["score"] = 9
            }), "The Native AOT upsert failed.");
            Require(collection.Delete(3), "The Native AOT delete failed.");
            Require(collection.FindById(3) is null, "The Native AOT delete verification failed.");
            Console.WriteLine("        Passed: upsert and delete.");
        }

        private static void RunStreamBackedScenario()
        {
            Console.WriteLine("  [2.1] Persist and read a document using a caller-owned MemoryStream.");
            using var stream = new MemoryStream();
            using var database = new LiteDatabase(stream);
            var collection = database.GetCollection("streamitems");
            collection.Insert(new BsonDocument
            {
                ["_id"] = 1,
                ["name"] = "stream-backed"
            });

            var item = collection.FindById(1);
            Require(item?["name"].AsString == "stream-backed", "The Native AOT stream-backed database round trip failed.");
            Console.WriteLine("        Passed: stream-backed persistence and readback.");
        }

        private static void RunGeneratedTypedMappingScenario(string databasePath)
        {
            Console.WriteLine("  [3.1] Register generated mappings plus a direct scalar execution map and round-trip a scalar typed record.");
            var mapper = new BsonMapper();
            LiteDbGeneratedMappings.Register(mapper);
            mapper.RegisterGeneratedExecutionMap(CreateAotSimpleExecutionMap());

            using var database = new LiteDatabase(databasePath, mapper);
            var simple = database.GetGeneratedCollection<AotSimpleRecord>("aot_simple");
            simple.Insert(new AotSimpleRecord { Name = "simple", Score = 7 });

            var simpleRead = simple.FindById(1);
            Require(simpleRead?.Name == "simple" && simpleRead.Score == 7,
                "The source-generated Native AOT simple typed round trip failed.");
            Console.WriteLine("        Passed: generated mapper registration, direct execution-map registration, and scalar typed round trip.");

            Console.WriteLine("  [3.1a] Evaluate a static-member LINQ expression through the generated mapper.");
            var staticMemberExpression = mapper.GetExpression<AotSimpleRecord, bool>(record => record.Score < DateTime.Today.Day + 1);
            var staticMemberResults = staticMemberExpression.Execute(new BsonDocument
            {
                ["_id"] = 2,
                ["Name"] = "interpreter",
                ["Score"] = 0
            }).ToArray();
            Require(staticMemberResults.Length == 1 && staticMemberResults[0].AsBoolean,
                "The source-generated Native AOT static-member LINQ expression evaluation failed.");
            Console.WriteLine("        Passed: static-member LINQ expression evaluation without runtime code generation.");

            Console.WriteLine("  [3.2] Update, count, and delete a generated typed record.");
            simpleRead.Name = "updated";
            Require(simple.Update(simpleRead), "The source-generated Native AOT typed update failed.");
            Require(simple.FindById(1)?.Name == "updated" && simple.Count() == 1,
                "The source-generated Native AOT typed update verification failed.");
            Require(simple.Delete(1) && simple.Count() == 0,
                "The source-generated Native AOT typed delete failed.");
            Console.WriteLine("        Passed: generated typed update, count, and delete.");

            mapper.SerializeNullValues = true;

            Console.WriteLine("  [3.3] Round-trip populated, null, and empty List<string> values.");
            Console.WriteLine("        Null values are persisted explicitly so the generated null-list mapping path is exercised.");
            var list = database.GetGeneratedCollection<AotListRecord>("aot_list");
            list.Insert(new AotListRecord
            {
                Id = 1,
                Name = "list",
                Values = ["one", "two"]
            });
            list.Insert(new AotListRecord { Id = 2, Name = "null-list", Values = null });
            list.Insert(new AotListRecord { Id = 3, Name = "empty-list", Values = [] });

            var listRead = list.FindById(1);
            var nullListRead = list.FindById(2);
            var emptyListRead = list.FindById(3);
            Require(listRead?.Name == "list" &&
                    listRead.Values is not null &&
                    listRead.Values.SequenceEqual(["one", "two"]),
                "The source-generated Native AOT List<string> typed round trip failed.");
            Require(nullListRead?.Values is null,
                "The source-generated Native AOT null List<string> round trip failed.");
            Require(emptyListRead?.Values is not null && emptyListRead.Values.Count == 0,
                "The source-generated Native AOT empty List<string> round trip failed.");
            Console.WriteLine("        Passed: populated, null, and empty List<string> round trips.");

            Console.WriteLine("  [3.4] Use a second generated model with the same mapper.");
            var secondary = database.GetGeneratedCollection<AotSecondaryRecord>("aot_secondary");
            secondary.Insert(new AotSecondaryRecord { Id = 10, Description = "secondary" });
            Require(secondary.FindById(10)?.Description == "secondary",
                "The source-generated Native AOT multi-model registration failed.");
            Console.WriteLine("        Passed: multiple generated models registered and used with one mapper.");

            Console.WriteLine("  [3.5] Round-trip DateTimeOffset values with exact ticks and offsets.");
            var expectedOccurredAt = new DateTimeOffset(2024, 6, 7, 8, 9, 10, TimeSpan.FromHours(-4)).AddTicks(4321);
            var expectedDeliveredAt = new DateTimeOffset(2024, 6, 8, 9, 10, 11, TimeSpan.FromHours(2)).AddTicks(1234);
            var dateTimeOffsets = database.GetGeneratedCollection<AotDateTimeOffsetRecord>("aot_date_time_offsets");
            dateTimeOffsets.Insert(new AotDateTimeOffsetRecord
            {
                Id = 20,
                OccurredAt = expectedOccurredAt,
                DeliveredAt = expectedDeliveredAt
            });

            var dateTimeOffsetRead = dateTimeOffsets.FindById(20);
            Require(dateTimeOffsetRead is not null &&
                    expectedOccurredAt.EqualsExact(dateTimeOffsetRead.OccurredAt) &&
                    dateTimeOffsetRead.DeliveredAt.HasValue &&
                    expectedDeliveredAt.EqualsExact(dateTimeOffsetRead.DeliveredAt.Value),
                "The source-generated Native AOT DateTimeOffset round trip failed.");
            Console.WriteLine("        Passed: required and nullable DateTimeOffset values retain their ticks and offsets.");

            Console.WriteLine("  [3.5a] Read a legacy BSON DateTime through the generated DateTimeOffset map.");
            var legacyDateTimeOffset = new DateTimeOffset(2024, 6, 9, 10, 11, 12, TimeSpan.FromHours(5.5)).AddTicks(4321);
            database.GetCollection("aot_date_time_offsets").Insert(new BsonDocument
            {
                ["_id"] = 21,
                [nameof(AotDateTimeOffsetRecord.OccurredAt)] = legacyDateTimeOffset.UtcDateTime
            });
            var legacyDateTimeOffsetRead = dateTimeOffsets.FindById(21);
            var expectedLegacyTicks = legacyDateTimeOffset.UtcDateTime.Ticks - (legacyDateTimeOffset.UtcDateTime.Ticks % TimeSpan.TicksPerMillisecond);
            Require(legacyDateTimeOffsetRead is not null &&
                    legacyDateTimeOffsetRead.OccurredAt.UtcDateTime.Ticks == expectedLegacyTicks &&
                    legacyDateTimeOffsetRead.OccurredAt.Offset == TimeSpan.Zero,
                "The source-generated Native AOT DateTimeOffset legacy BSON DateTime read failed.");
            Console.WriteLine("        Passed: legacy BSON DateTime materializes as a UTC DateTimeOffset at BSON DateTime precision.");

            Console.WriteLine("  [3.6] Round-trip the remaining native scalar conversion boundaries.");
            var expectedObjectId = new ObjectId("64c61e5f18a9421a8862c71c");
            var expectedTimestamp = new DateTime(2024, 6, 9, 10, 11, 12, 123, DateTimeKind.Utc);
            var expectedTimestampWithOffset = new DateTimeOffset(2024, 6, 10, 11, 12, 13, TimeSpan.FromHours(5.5)).AddTicks(4321);
            var expectedPayload = new byte[] { 0, 1, 127, 128, 255 };
            var nativeScalars = database.GetGeneratedCollection<AotNativeScalarRecord>("aot_native_scalars");
            nativeScalars.Insert(new AotNativeScalarRecord
            {
                Id = 30,
                BooleanValue = true,
                ByteValue = 200,
                SignedByteValue = -100,
                Character = '\u03BB',
                SignedShort = -12_345,
                UnsignedShort = 54_321,
                SignedInteger = -1_234_567_890,
                UnsignedInteger = 3_000_000_000U,
                SignedLong = -8_000_000_000_000_000_000L,
                UnsignedLong = 9_000_000_000_000_000_000UL,
                SingleValue = 123.5f,
                DoubleValue = 456.25d,
                DecimalValue = 789.125m,
                State = AotNativeScalarState.Captured,
                ObjectId = expectedObjectId,
                Timestamp = expectedTimestamp,
                TimestampWithOffset = expectedTimestampWithOffset,
                Payload = expectedPayload,
                CorrelationId = new Guid("09e72680-2f4f-4eb3-a70c-f27d489b6068"),
                Name = "native-scalars"
            });

            var nativeScalarRead = nativeScalars.FindById(30);
            Require(nativeScalarRead is not null,
                "The source-generated Native AOT native scalar record was not found after insertion.");

            RequireNativeScalar("BooleanValue", nativeScalarRead.BooleanValue, "True", nativeScalarRead.BooleanValue.ToString());
            RequireNativeScalar("ByteValue", nativeScalarRead.ByteValue == 200, "200", nativeScalarRead.ByteValue.ToString());
            RequireNativeScalar("SignedByteValue", nativeScalarRead.SignedByteValue == -100, "-100", nativeScalarRead.SignedByteValue.ToString());
            RequireNativeScalar("Character", nativeScalarRead.Character == '\u03BB', "U+03BB", $"U+{(int)nativeScalarRead.Character:X4}");
            RequireNativeScalar("SignedShort", nativeScalarRead.SignedShort == -12_345, "-12345", nativeScalarRead.SignedShort.ToString());
            RequireNativeScalar("UnsignedShort", nativeScalarRead.UnsignedShort == 54_321, "54321", nativeScalarRead.UnsignedShort.ToString());
            RequireNativeScalar("SignedInteger", nativeScalarRead.SignedInteger == -1_234_567_890, "-1234567890", nativeScalarRead.SignedInteger.ToString());
            RequireNativeScalar("UnsignedInteger", nativeScalarRead.UnsignedInteger == 3_000_000_000U, "3000000000", nativeScalarRead.UnsignedInteger.ToString());
            RequireNativeScalar("SignedLong", nativeScalarRead.SignedLong == -8_000_000_000_000_000_000L, "-8000000000000000000", nativeScalarRead.SignedLong.ToString());
            RequireNativeScalar("UnsignedLong", nativeScalarRead.UnsignedLong == 9_000_000_000_000_000_000UL, "9000000000000000000", nativeScalarRead.UnsignedLong.ToString());
            RequireNativeScalar("SingleValue", nativeScalarRead.SingleValue == 123.5f, "123.5", nativeScalarRead.SingleValue.ToString());
            RequireNativeScalar("DoubleValue", nativeScalarRead.DoubleValue == 456.25d, "456.25", nativeScalarRead.DoubleValue.ToString());
            RequireNativeScalar("DecimalValue", nativeScalarRead.DecimalValue == 789.125m, "789.125", nativeScalarRead.DecimalValue.ToString());
            RequireNativeScalar("State", nativeScalarRead.State == AotNativeScalarState.Captured, nameof(AotNativeScalarState.Captured), nativeScalarRead.State.ToString());
            RequireNativeScalar("ObjectId", nativeScalarRead.ObjectId == expectedObjectId, expectedObjectId.ToString(), nativeScalarRead.ObjectId.ToString());
            RequireNativeScalar(
                "Timestamp",
                nativeScalarRead.Timestamp.ToUniversalTime() == expectedTimestamp,
                expectedTimestamp.ToString("O"),
                $"{nativeScalarRead.Timestamp:O} (UTC: {nativeScalarRead.Timestamp.ToUniversalTime():O})");
            RequireNativeScalar("TimestampWithOffset", expectedTimestampWithOffset.EqualsExact(nativeScalarRead.TimestampWithOffset), expectedTimestampWithOffset.ToString("O"), nativeScalarRead.TimestampWithOffset.ToString("O"));
            RequireNativeScalar("Payload", nativeScalarRead.Payload.SequenceEqual(expectedPayload), Convert.ToHexString(expectedPayload), Convert.ToHexString(nativeScalarRead.Payload));
            RequireNativeScalar("CorrelationId", nativeScalarRead.CorrelationId == new Guid("09e72680-2f4f-4eb3-a70c-f27d489b6068"), "09e72680-2f4f-4eb3-a70c-f27d489b6068", nativeScalarRead.CorrelationId.ToString());
            RequireNativeScalar("Name", nativeScalarRead.Name == "native-scalars", "native-scalars", nativeScalarRead.Name);
            Console.WriteLine("        Passed: all native scalar conversion boundaries.");

            Console.WriteLine("  [3.7] Round-trip inherited generated properties and mapping attributes.");
            var inheritedRecords = database.GetGeneratedCollection<AotInheritedRecord>("aot_inherited_records");
            inheritedRecords.Insert(new AotInheritedRecord
            {
                BaseId = 40,
                BaseName = "base-value",
                BaseTags = ["first", "second"],
                IgnoredBaseValue = "not persisted",
                DerivedName = "derived-value"
            });

            var inheritedRead = inheritedRecords.FindById(40);
            Require(inheritedRead is not null &&
                    inheritedRead.BaseId == 40 &&
                    inheritedRead.BaseName == "base-value" &&
                    inheritedRead.BaseTags.SequenceEqual(["first", "second"]) &&
                    inheritedRead.IgnoredBaseValue is null &&
                    inheritedRead.DerivedName == "derived-value" &&
                    inheritedRead.Fingerprint == "base-value|derived-value",
                "The source-generated Native AOT inherited-property round trip failed.");
            Console.WriteLine("        Passed: inherited ID, named field, ignored member, list, derived property, and computed fingerprint round trip.");

            Console.WriteLine("  [3.7a] Round-trip a mutable record and a multi-level virtual override.");
            var mutableRecords = database.GetGeneratedCollection<AotMutableRecord>("aot_mutable_records");
            mutableRecords.Insert(new AotMutableRecord { Name = "record" });
            var overrideRecords = database.GetGeneratedCollection<AotOverrideRecord>("aot_override_records");
            overrideRecords.Insert(new AotOverrideRecord { OverrideId = 41, Name = "override" });
            var overrideRead = overrideRecords.FindById(41);
            Require(mutableRecords.FindById(1)?.Name == "record" &&
                    overrideRead is not null &&
                    overrideRead.OverrideId == 41 &&
                    overrideRead.SetterCalls == 1 &&
                    overrideRead.Name == "override",
                "The source-generated Native AOT record or virtual-override round trip failed.");
            Console.WriteLine("        Passed: mutable record construction and most-derived virtual-property materialization.");

            Console.WriteLine("  [3.8] Round-trip populated and null nullable scalar values.");
            var expectedNullableTimestamp = new DateTime(2024, 7, 6, 8, 9, 10, 123, DateTimeKind.Utc);
            var expectedNullableCorrelationId = new Guid("5e3e59bf-c079-46cf-98f6-8287ab7c69cc");
            var nullableScalars = database.GetGeneratedCollection<AotNullableScalarRecord>("aot_nullable_scalars");
            nullableScalars.Insert(new AotNullableScalarRecord
            {
                Id = 50,
                ProcessId = 8128,
                IsElevated = false,
                State = AotNativeScalarState.Captured,
                CorrelationId = expectedNullableCorrelationId,
                RecordedAt = expectedNullableTimestamp
            });
            nullableScalars.Insert(new AotNullableScalarRecord { Id = 51 });

            var populatedNullableScalars = nullableScalars.FindById(50);
            var nullNullableScalars = nullableScalars.FindById(51);
            Require(populatedNullableScalars is not null &&
                    populatedNullableScalars.ProcessId == 8128 &&
                    populatedNullableScalars.IsElevated == false &&
                    populatedNullableScalars.State == AotNativeScalarState.Captured &&
                    populatedNullableScalars.CorrelationId == expectedNullableCorrelationId &&
                    populatedNullableScalars.RecordedAt.HasValue &&
                    populatedNullableScalars.RecordedAt.Value.ToUniversalTime() == expectedNullableTimestamp,
                "The source-generated Native AOT populated nullable-scalar round trip failed.");
            Require(nullNullableScalars is not null &&
                    nullNullableScalars.ProcessId is null &&
                    nullNullableScalars.IsElevated is null &&
                    nullNullableScalars.State is null &&
                    nullNullableScalars.CorrelationId is null &&
                    nullNullableScalars.RecordedAt is null,
                "The source-generated Native AOT null nullable-scalar round trip failed.");
            Console.WriteLine("        Passed: populated and BSON-null nullable integer, Boolean, enum, GUID, and DateTime values.");

            Console.WriteLine("  [3.9] Round-trip populated, null, and empty string arrays.");
            var stringArrays = database.GetGeneratedCollection<AotStringArrayRecord>("aot_string_arrays");
            stringArrays.Insert(new AotStringArrayRecord { Id = 60, StreamNames = ["primary", "metadata"] });
            stringArrays.Insert(new AotStringArrayRecord { Id = 61, StreamNames = null });
            stringArrays.Insert(new AotStringArrayRecord { Id = 62, StreamNames = [] });

            var populatedStringArrays = stringArrays.FindById(60);
            var nullStringArrays = stringArrays.FindById(61);
            var emptyStringArrays = stringArrays.FindById(62);
            Require(populatedStringArrays is not null &&
                    populatedStringArrays.StreamNames is not null &&
                    populatedStringArrays.StreamNames.SequenceEqual(["primary", "metadata"]),
                "The source-generated Native AOT populated string-array round trip failed.");
            Require(nullStringArrays is not null && nullStringArrays.StreamNames is null,
                "The source-generated Native AOT null string-array round trip failed.");
            Require(emptyStringArrays is not null &&
                    emptyStringArrays.StreamNames is not null &&
                    emptyStringArrays.StreamNames.Length == 0,
                "The source-generated Native AOT empty string-array round trip failed.");
            Console.WriteLine("        Passed: populated, BSON-null, and empty string-array round trips.");

            Console.WriteLine("  [3.10] Round-trip BSON-native dynamic dictionary values.");
            var dynamicDictionaries = database.GetGeneratedCollection<AotDynamicDictionaryRecord>("aot_dynamic_dictionaries");
            dynamicDictionaries.Insert(new AotDynamicDictionaryRecord
            {
                Id = 70,
                Fields = new Dictionary<string, object>
                {
                    ["message"] = "payload",
                    ["attempt"] = 3,
                    ["enabled"] = false,
                    ["missing"] = null,
                    ["nested"] = new Dictionary<string, object>
                    {
                        ["inner"] = "value"
                    },
                    ["items"] = new object[] { "first", 2, null }
                }
            });

            var dynamicDictionaryRead = dynamicDictionaries.FindById(70);
            Require(dynamicDictionaryRead is not null &&
                    dynamicDictionaryRead.Fields["message"] as string == "payload" &&
                    (int)dynamicDictionaryRead.Fields["attempt"] == 3 &&
                    (bool)dynamicDictionaryRead.Fields["enabled"] == false &&
                    dynamicDictionaryRead.Fields["missing"] is null &&
                    ((Dictionary<string, object>)dynamicDictionaryRead.Fields["nested"])["inner"] as string == "value" &&
                    ((object[])dynamicDictionaryRead.Fields["items"])[0] as string == "first" &&
                    (int)((object[])dynamicDictionaryRead.Fields["items"])[1] == 2 &&
                    ((object[])dynamicDictionaryRead.Fields["items"])[2] is null,
                "The source-generated Native AOT dynamic dictionary round trip failed.");
            Console.WriteLine("        Passed: BSON-native scalar, null, nested document, and nested array dictionary values.");

            Console.WriteLine("  [3.11] Round-trip DateTimeOffset offset directions and raw BSON document shape.");
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
                    expectedPositiveOffset.EqualsExact(dateTimeOffsetBoundaryRead.PositiveOffset) &&
                    expectedNegativeOffset.EqualsExact(dateTimeOffsetBoundaryRead.NegativeOffset) &&
                    dateTimeOffsetBoundaryRead.NullableOffset.HasValue &&
                    expectedNullableOffset.EqualsExact(dateTimeOffsetBoundaryRead.NullableOffset.Value),
                "The source-generated Native AOT DateTimeOffset boundary round trip failed.");
            Require(nullDateTimeOffsetBoundaryRead is not null && nullDateTimeOffsetBoundaryRead.NullableOffset is null,
                "The source-generated Native AOT nullable DateTimeOffset BSON-null round trip failed.");
            RequireDateTimeOffsetDocument(dateTimeOffsetBoundaryDocument[nameof(AotDateTimeOffsetBoundaryRecord.PositiveOffset)], expectedPositiveOffset);
            RequireDateTimeOffsetDocument(dateTimeOffsetBoundaryDocument[nameof(AotDateTimeOffsetBoundaryRecord.NegativeOffset)], expectedNegativeOffset);
            RequireDateTimeOffsetDocument(dateTimeOffsetBoundaryDocument[nameof(AotDateTimeOffsetBoundaryRecord.NullableOffset)], expectedNullableOffset);
            Console.WriteLine("        Passed: positive, negative, and nullable offsets retain exact ticks, offsets, and BSON document fields.");

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
                    expectedNullableOffsetScalar.EqualsExact(populatedNullableScalarBoundaries.TimestampWithOffset.Value),
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
            var stringArrayBoundaries = database.GetGeneratedCollection<AotStringArrayRecord>("aot_string_array_boundaries");
            stringArrayBoundaries.Insert(new AotStringArrayRecord { Id = 110, StreamNames = [string.Empty] });
            stringArrayBoundaries.Insert(new AotStringArrayRecord { Id = 111, StreamNames = expectedStringArrayBoundary });

            var singleStringArrayBoundary = stringArrayBoundaries.FindById(110);
            var manyStringArrayBoundary = stringArrayBoundaries.FindById(111);
            var manyStringArrayBoundaryDocument = database.GetCollection("aot_string_array_boundaries").FindById(111);
            Require(singleStringArrayBoundary is not null &&
                    singleStringArrayBoundary.StreamNames is not null &&
                    singleStringArrayBoundary.StreamNames.SequenceEqual([string.Empty]) &&
                    manyStringArrayBoundary is not null &&
                    manyStringArrayBoundary.StreamNames is not null &&
                    manyStringArrayBoundary.StreamNames.SequenceEqual(expectedStringArrayBoundary) &&
                    manyStringArrayBoundaryDocument[nameof(AotStringArrayRecord.StreamNames)].AsArray.Count == expectedStringArrayBoundary.Length &&
                    manyStringArrayBoundaryDocument[nameof(AotStringArrayRecord.StreamNames)].AsArray[63].AsString == "last",
                "The source-generated Native AOT string-array boundary round trip failed.");
            Console.WriteLine("        Passed: single empty-string element, bounded array length, BSON array shape, and element order.");

            Console.WriteLine("  [3.15] Round-trip recursive dynamic dictionaries and reject unsupported values.");
            var dynamicDictionaryBoundaries = database.GetGeneratedCollection<AotDynamicDictionaryRecord>("aot_dynamic_dictionary_boundaries");
            dynamicDictionaryBoundaries.Insert(new AotDynamicDictionaryRecord { Id = 120, Fields = [] });
            dynamicDictionaryBoundaries.Insert(new AotDynamicDictionaryRecord
            {
                Id = 121,
                Fields = new Dictionary<string, object>
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
                    ((Dictionary<string, object>)populatedDynamicDictionary.Fields["rawDocument"])["kind"] as string == "raw" &&
                    ((object[])populatedDynamicDictionary.Fields["rawArray"])[0] as string == "first" &&
                    (int)((object[])populatedDynamicDictionary.Fields["rawArray"])[1] == 2 &&
                    ((Dictionary<string, object>)((object[])populatedDynamicDictionary.Fields["nestedArray"])[0])["inner"] as string == "value" &&
                    ((object[])((object[])populatedDynamicDictionary.Fields["nestedArray"])[1])[0] as string == "nested" &&
                    (int)((object[])((object[])populatedDynamicDictionary.Fields["nestedArray"])[1])[1] == 4 &&
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
                Fields = new Dictionary<string, object>
                {
                    ["unsupported"] = new DateTimeOffset(2024, 7, 12, 13, 14, 15, TimeSpan.Zero)
                }
            }), "The source-generated Native AOT dynamic dictionary accepted DateTimeOffset without a schema.");
            Console.WriteLine("        Passed: empty, numeric, raw BSON, recursive dynamic values, and unsupported-value rejection.");
        }

        private static void RunScenario(string name, Action scenario)
        {
            Console.WriteLine($"[SCENARIO] {name}");
            scenario();
            Console.WriteLine($"[PASS] {name}");
            Console.WriteLine();
        }

        private static void RequireDateTimeOffsetDocument(BsonValue value, DateTimeOffset expected)
        {
            Require(value.IsDocument &&
                    value.AsDocument["DateTime"].AsInt64 == expected.Ticks &&
                    value.AsDocument["Offset"].AsInt64 == expected.Offset.Ticks,
                "The source-generated Native AOT DateTimeOffset BSON document shape was not preserved.");
        }

        private static void RequireThrows<TException>(Action action, string message)
            where TException : Exception
        {
            try
            {
                action();
            }
            catch (TException)
            {
                return;
            }

            throw new InvalidOperationException(message);
        }

        private static void RequireNativeScalar(string field, bool condition, string expected, string actual)
        {
            Require(condition,
                $"The source-generated Native AOT native scalar round trip failed for '{field}'. Expected: '{expected}'. Actual: '{actual}'.");
            Console.WriteLine($"        Passed: {field}.");
        }

        private static GeneratedEntityMap<AotSimpleRecord> CreateAotSimpleExecutionMap()
        {
            return new GeneratedEntityMap<AotSimpleRecord>(
                record => new BsonDocument
                {
                    ["_id"] = record.Id,
                    [nameof(AotSimpleRecord.Name)] = record.Name,
                    [nameof(AotSimpleRecord.Score)] = record.Score
                },
                document => new AotSimpleRecord
                {
                    Id = document["_id"].AsInt32,
                    Name = document[nameof(AotSimpleRecord.Name)].AsString,
                    Score = document[nameof(AotSimpleRecord.Score)].AsInt32
                });
        }

        private static void Require(bool condition, string message)
        {
            if (!condition)
            {
                throw new InvalidOperationException(message);
            }
        }
    }

    [BsonSourceGenerated]
    public sealed record AotMutableRecord
    {
        public int Id { get; set; }
        public string Name { get; set; } = string.Empty;
    }

    public class AotOverrideBase
    {
        [BsonId(false)]
        public virtual int OverrideId { get; set; }
    }

    public class AotOverrideMiddle : AotOverrideBase
    {
        public override int OverrideId { get; set; }
    }

    [BsonSourceGenerated]
    public sealed class AotOverrideRecord : AotOverrideMiddle
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

        public string Name { get; set; } = string.Empty;

        [BsonIgnore]
        public int SetterCalls { get; private set; }
    }

    [BsonSourceGenerated]
    public sealed class AotSimpleRecord
    {
        public int Id { get; set; }
        public string Name { get; set; } = string.Empty;
        public int Score { get; set; }
    }

    [BsonSourceGenerated]
    public sealed class AotListRecord
    {
        public int Id { get; set; }
        public string Name { get; set; } = string.Empty;
        public List<string> Values { get; set; } = [];
    }

    [BsonSourceGenerated]
    public sealed class AotDynamicDictionaryRecord
    {
        public int Id { get; set; }
        public Dictionary<string, object> Fields { get; set; } = [];
    }

    [BsonSourceGenerated]
    public sealed class AotStringArrayRecord
    {
        public int Id { get; set; }
        public string[] StreamNames { get; set; }
    }

    [BsonSourceGenerated]
    public sealed class AotNullableScalarRecord
    {
        public int Id { get; set; }
        public int? ProcessId { get; set; }
        public bool? IsElevated { get; set; }
        public AotNativeScalarState? State { get; set; }
        public Guid? CorrelationId { get; set; }
        public DateTime? RecordedAt { get; set; }
    }

    public abstract class AotInheritedRecordBase
    {
        [BsonId(false)]
        public int BaseId { get; set; }

        [BsonField("base_name")]
        public string BaseName { get; set; } = string.Empty;

        public List<string> BaseTags { get; set; } = [];

        [BsonIgnore]
        public string IgnoredBaseValue { get; set; }
    }

    [BsonSourceGenerated]
    public sealed class AotInheritedRecord : AotInheritedRecordBase
    {
        public string DerivedName { get; set; } = string.Empty;
        public string Fingerprint => string.Join("|", BaseName, DerivedName);
    }

    [BsonSourceGenerated]
    public sealed class AotNativeScalarRecord
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
        public AotNativeScalarState State { get; set; }
        public ObjectId ObjectId { get; set; } = ObjectId.Empty;
        public DateTime Timestamp { get; set; }
        public DateTimeOffset TimestampWithOffset { get; set; }
        public byte[] Payload { get; set; } = [];
        public Guid CorrelationId { get; set; }
        public string Name { get; set; } = string.Empty;
    }

    public enum AotNativeScalarState
    {
        Unknown = 0,
        Captured = 17
    }

    [BsonSourceGenerated]
    public sealed class AotDateTimeOffsetRecord
    {
        public int Id { get; set; }
        public DateTimeOffset OccurredAt { get; set; }
        public DateTimeOffset? DeliveredAt { get; set; }
    }

    [BsonSourceGenerated]
    public sealed class AotDateTimeOffsetBoundaryRecord
    {
        public int Id { get; set; }
        public DateTimeOffset PositiveOffset { get; set; }
        public DateTimeOffset NegativeOffset { get; set; }
        public DateTimeOffset? NullableOffset { get; set; }
    }

    [BsonSourceGenerated]
    public sealed class AotNullableScalarBoundaryRecord
    {
        public int Id { get; set; }
        public short? SignedShort { get; set; }
        public ulong? UnsignedLong { get; set; }
        public double? Ratio { get; set; }
        public decimal? Amount { get; set; }
        public DateTimeOffset? TimestampWithOffset { get; set; }
    }

    public abstract class AotMultiLevelInheritedGrandparent
    {
        [BsonId(false)]
        public int RootId { get; set; }

        [BsonField("origin")]
        public string Origin { get; set; } = string.Empty;
    }

    public abstract class AotMultiLevelInheritedParent : AotMultiLevelInheritedGrandparent
    {
        public string ParentName { get; set; } = string.Empty;
        public List<string> Values { get; set; } = [];

        [BsonIgnore]
        public string IgnoredParentValue { get; set; }
    }

    [BsonSourceGenerated]
    public sealed class AotMultiLevelInheritedRecord : AotMultiLevelInheritedParent
    {
        public string DerivedName { get; set; } = string.Empty;
        public string Fingerprint => string.Join("|", Origin, DerivedName, string.Join(",", Values));
        public int ValueCount => Values.Count;
    }

    internal sealed class UnsupportedAotDynamicDictionaryValue
    {
    }

    [BsonSourceGenerated]
    public sealed class AotSecondaryRecord
    {
        public int Id { get; set; }
        public string Description { get; set; } = string.Empty;
    }
}
