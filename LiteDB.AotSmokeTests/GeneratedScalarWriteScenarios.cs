using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

using LiteDB.Generated;
using static LiteDB.AotSmokeTests.SmokeAssert;

#nullable enable
namespace LiteDB.AotSmokeTests
{
    internal static class GeneratedScalarWriteScenarios
    {
        internal static void Run(LiteDatabase database)
        {
            Console.WriteLine("  [3.2b] Round-trip the scalar compatibility matrix without a manual execution map.");
            var expectedObjectId = new ObjectId("64c61e5f18a9421a8862c71c");
            var expectedCorrelationId = new Guid("d29368bb-9669-4f84-9384-c8eb15caa0a8");
            var scalarCompatibility = database.GetGeneratedCollection<AotScalarCompatibilityRecord>("aot_scalar_compatibility");
            scalarCompatibility.Insert(new AotScalarCompatibilityRecord
            {
                Id = 1,
                BooleanValue = true,
                UnsignedInteger = uint.MaxValue,
                SignedLong = -9_000_000_000L,
                UnsignedLong = ulong.MaxValue,
                DecimalValue = 7.75m,
                State = AotNativeScalarState.Captured,
                Timestamp = new DateTime(2024, 8, 1, 12, 34, 56, 789, DateTimeKind.Utc),
                ObjectId = expectedObjectId,
                CorrelationId = expectedCorrelationId,
                Payload = [0, 1, 127, 128, 255],
                Name = "  scalar compatibility  ",
                NullableState = null,
                NullablePayload = null
            });
            var scalarDocument = database.GetCollection("aot_scalar_compatibility").FindById(1);
            var scalarRead = scalarCompatibility.FindById(1);
            Require(scalarDocument[nameof(AotScalarCompatibilityRecord.UnsignedInteger)].Type == BsonType.Int64 &&
                    scalarDocument[nameof(AotScalarCompatibilityRecord.State)].Type == BsonType.String &&
                    scalarDocument[nameof(AotScalarCompatibilityRecord.State)].AsString == nameof(AotNativeScalarState.Captured) &&
                    scalarDocument[nameof(AotScalarCompatibilityRecord.NullableState)].IsNull &&
                    scalarDocument[nameof(AotScalarCompatibilityRecord.NullablePayload)].IsNull &&
                    scalarRead is not null &&
                    scalarRead.BooleanValue &&
                    scalarRead.UnsignedInteger == uint.MaxValue &&
                    scalarRead.SignedLong == -9_000_000_000L &&
                    scalarRead.UnsignedLong == ulong.MaxValue &&
                    scalarRead.DecimalValue == 7.75m &&
                    scalarRead.State == AotNativeScalarState.Captured &&
                    scalarRead.ObjectId == expectedObjectId &&
                    scalarRead.CorrelationId == expectedCorrelationId &&
                    scalarRead.Payload.SequenceEqual(new byte[] { 0, 1, 127, 128, 255 }) &&
                    scalarRead.Name == "scalar compatibility" &&
                    scalarRead.NullableState is null &&
                    scalarRead.NullablePayload is null,
                "The source-generated Native AOT scalar compatibility map failed.");
            Console.WriteLine("        Passed: generated scalar conversion, BSON shape, nullable values, and mapper options without manual registration.");

            Console.WriteLine("  [3.2c] Execute explicit-ID and batch scalar writes without a manual execution map.");
            var generatedWrites = database.GetGeneratedCollection<AotGeneratedScalarRecord>("aot_scalar_writes");
            var explicitWrite = new AotGeneratedScalarRecord { Id = 900, Name = "explicit", Score = 1 };
            generatedWrites.Insert(41, explicitWrite);
            var batchWrites = new[]
            {
                new AotGeneratedScalarRecord { Name = "batch-first", Score = 2 },
                new AotGeneratedScalarRecord { Name = "batch-second", Score = 3 }
            };
            generatedWrites.Insert(batchWrites);
            batchWrites[0].Score = 20;
            batchWrites[1].Score = 30;
            var batchUpdateCount = generatedWrites.Update(batchWrites);
            var explicitUpdate = new AotGeneratedScalarRecord { Id = 999, Name = "explicit-update", Score = 40 };
            var explicitUpdateResult = generatedWrites.Update(41, explicitUpdate);
            Require(explicitWrite.Id == 900 &&
                    generatedWrites.FindById(41)?.Name == "explicit-update" &&
                    explicitUpdate.Id == 999 &&
                    batchWrites[0].Id != 0 &&
                    batchWrites[1].Id != 0 &&
                    batchWrites[0].Id != batchWrites[1].Id &&
                    batchUpdateCount == 2 &&
                    generatedWrites.FindById(batchWrites[0].Id)?.Score == 20 &&
                    generatedWrites.FindById(batchWrites[1].Id)?.Score == 30 &&
                    explicitUpdateResult,
                "The source-generated Native AOT generated batch explicit-ID or batch scalar write failed.");
            Console.WriteLine("        Passed: generated explicit-ID and batch insert/update paths work without manual registration.");

            Console.WriteLine("  [3.2d] Execute automatic-ID, batch, and explicit-ID upserts without a manual execution map.");
            var generatedUpserts = database.GetGeneratedCollection<AotGeneratedScalarRecord>("aot_scalar_upserts");
            var automaticUpsert = new AotGeneratedScalarRecord { Name = "automatic", Score = 1 };
            var automaticInsert = generatedUpserts.Upsert(automaticUpsert);
            automaticUpsert.Name = "automatic-updated";
            automaticUpsert.Score = 2;
            var automaticUpdate = generatedUpserts.Upsert(automaticUpsert);
            var batchUpserts = new[]
            {
                new AotGeneratedScalarRecord { Name = "batch-first", Score = 3 },
                new AotGeneratedScalarRecord { Name = "batch-second", Score = 4 }
            };
            var batchInsertCount = generatedUpserts.Upsert(batchUpserts);
            batchUpserts[0].Score = 30;
            batchUpserts[1].Score = 40;
            var batchUpsertUpdateCount = generatedUpserts.Upsert(batchUpserts);
            var explicitUpsert = new AotGeneratedScalarRecord { Id = 900, Name = "explicit", Score = 5 };
            var explicitInsert = generatedUpserts.Upsert(41, explicitUpsert);
            explicitUpsert.Name = "explicit-updated";
            explicitUpsert.Score = 50;
            var explicitUpsertUpdate = generatedUpserts.Upsert(41, explicitUpsert);
            Require(automaticInsert &&
                    !automaticUpdate &&
                    automaticUpsert.Id != 0 &&
                    generatedUpserts.FindById(automaticUpsert.Id)?.Score == 2 &&
                    batchInsertCount == 2 &&
                    batchUpsertUpdateCount == 0 &&
                    batchUpserts[0].Id != 0 &&
                    batchUpserts[1].Id != 0 &&
                    batchUpserts[0].Id != batchUpserts[1].Id &&
                    generatedUpserts.FindById(batchUpserts[0].Id)?.Score == 30 &&
                    generatedUpserts.FindById(batchUpserts[1].Id)?.Score == 40 &&
                    explicitInsert &&
                    !explicitUpsertUpdate &&
                    explicitUpsert.Id == 900 &&
                    generatedUpserts.FindById(41)?.Name == "explicit-updated",
                "The source-generated Native AOT generated scalar upsert behavior failed.");
            Console.WriteLine("        Passed: generated scalar upserts preserve generated IDs, explicit IDs, and insert-count return semantics.");

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

            Console.WriteLine("  [3.5] Round-trip DateTimeOffset values using canonical UTC BSON precision.");
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
                    IsCanonicalDateTimeOffset(expectedOccurredAt, dateTimeOffsetRead.OccurredAt) &&
                    dateTimeOffsetRead.DeliveredAt.HasValue &&
                    IsCanonicalDateTimeOffset(expectedDeliveredAt, dateTimeOffsetRead.DeliveredAt.Value),
                "The source-generated Native AOT DateTimeOffset round trip failed.");
            Console.WriteLine("        Passed: required and nullable DateTimeOffset values use canonical UTC BSON precision.");

            Console.WriteLine("  [3.5a] Read an ordinary BSON DateTime through the generated DateTimeOffset map.");
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
                "The source-generated Native AOT DateTimeOffset BSON DateTime read failed.");
            Console.WriteLine("        Passed: BSON DateTime materializes as a UTC DateTimeOffset at BSON DateTime precision.");

        }
    }
}
