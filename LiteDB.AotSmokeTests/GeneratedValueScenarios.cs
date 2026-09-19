using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

using LiteDB.Generated;
using static LiteDB.AotSmokeTests.SmokeAssert;

#nullable enable
namespace LiteDB.AotSmokeTests
{
    internal static class GeneratedValueScenarios
    {
        internal static void Run(LiteDatabase database)
        {
            var employees = database.GetGeneratedCollection<AotEmployee>("aot_employee_ids");
            var employee = new AotEmployee { AotPersonId = 42, Name = "Ada" };
            Require(employees.Insert(employee).AsInt32 == 42, "Inherited conventional ID was not stored as _id.");
            var automatic = new AotEmployee { Name = "Grace" };
            employees.Insert(automatic);
            Require(automatic.AotPersonId == 43 && employees.FindById(43).Name == "Grace",
                "Inherited conventional auto-ID was not assigned or hydrated.");
            SmokeAssert.Report("generated-inherited-id", new BsonDocument
            {
                ["explicit"] = employees.FindOne(x => x.AotPersonId == 42).AotPersonId,
                ["automatic"] = automatic.AotPersonId
            });

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

            RequireNativeScalar("BooleanValue", nativeScalarRead!.BooleanValue, "True", nativeScalarRead.BooleanValue.ToString());
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
            RequireNativeScalar("TimestampWithOffset", IsCanonicalDateTimeOffset(expectedTimestampWithOffset, nativeScalarRead.TimestampWithOffset), expectedTimestampWithOffset.ToString("O"), nativeScalarRead.TimestampWithOffset.ToString("O"));
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
                Fields = new Dictionary<string, object?>
                {
                    ["message"] = "payload",
                    ["attempt"] = 3,
                    ["enabled"] = false,
                    ["missing"] = null,
                    ["nested"] = new Dictionary<string, object>
                    {
                        ["inner"] = "value"
                    },
                    ["items"] = new object?[] { "first", 2, null }
                }
            });

            var dynamicDictionaryRead = dynamicDictionaries.FindById(70);
            Require(dynamicDictionaryRead is not null &&
                    (dynamicDictionaryRead.Fields["message"] as string) == "payload" &&
                    (int)dynamicDictionaryRead.Fields["attempt"] == 3 &&
                    (bool)dynamicDictionaryRead.Fields["enabled"] == false &&
                    dynamicDictionaryRead.Fields["missing"] is null &&
                    (((Dictionary<string, object>?)dynamicDictionaryRead.Fields["nested"])["inner"] as string) == "value" &&
                    (((object[]?)dynamicDictionaryRead.Fields["items"])[0] as string) == "first" &&
                    (int)((object[]?)dynamicDictionaryRead.Fields["items"])[1] == 2 &&
                    ((object[]?)dynamicDictionaryRead.Fields["items"])[2] is null,
                "The source-generated Native AOT dynamic dictionary round trip failed.");
            Console.WriteLine("        Passed: BSON-native scalar, null, nested document, and nested array dictionary values.");

        }
    }
}
