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
            Console.WriteLine("  [3.2b] Automatically round-trip the C2 scalar compatibility matrix without a manual execution map.");
            var expectedC2ObjectId = new ObjectId("64c61e5f18a9421a8862c71c");
            var expectedC2CorrelationId = new Guid("d29368bb-9669-4f84-9384-c8eb15caa0a8");
            var automaticC2 = database.GetGeneratedCollection<AotPhaseCScalarCompatibilityRecord>("aot_phase_c_scalar_compatibility");
            automaticC2.Insert(new AotPhaseCScalarCompatibilityRecord
            {
                Id = 1,
                BooleanValue = true,
                UnsignedInteger = uint.MaxValue,
                SignedLong = -9_000_000_000L,
                UnsignedLong = ulong.MaxValue,
                DecimalValue = 7.75m,
                State = AotNativeScalarState.Captured,
                Timestamp = new DateTime(2024, 8, 1, 12, 34, 56, 789, DateTimeKind.Utc),
                ObjectId = expectedC2ObjectId,
                CorrelationId = expectedC2CorrelationId,
                Payload = [0, 1, 127, 128, 255],
                Name = "  c2 compatibility  ",
                NullableState = null,
                NullablePayload = null
            });
            var automaticC2Document = database.GetCollection("aot_phase_c_scalar_compatibility").FindById(1);
            var automaticC2Read = automaticC2.FindById(1);
            Require(automaticC2Document[nameof(AotPhaseCScalarCompatibilityRecord.UnsignedInteger)].Type == BsonType.Int64 &&
                    automaticC2Document[nameof(AotPhaseCScalarCompatibilityRecord.State)].Type == BsonType.String &&
                    automaticC2Document[nameof(AotPhaseCScalarCompatibilityRecord.State)].AsString == nameof(AotNativeScalarState.Captured) &&
                    automaticC2Document[nameof(AotPhaseCScalarCompatibilityRecord.NullableState)].IsNull &&
                    automaticC2Document[nameof(AotPhaseCScalarCompatibilityRecord.NullablePayload)].IsNull &&
                    automaticC2Read is not null &&
                    automaticC2Read.BooleanValue &&
                    automaticC2Read.UnsignedInteger == uint.MaxValue &&
                    automaticC2Read.SignedLong == -9_000_000_000L &&
                    automaticC2Read.UnsignedLong == ulong.MaxValue &&
                    automaticC2Read.DecimalValue == 7.75m &&
                    automaticC2Read.State == AotNativeScalarState.Captured &&
                    automaticC2Read.ObjectId == expectedC2ObjectId &&
                    automaticC2Read.CorrelationId == expectedC2CorrelationId &&
                    automaticC2Read.Payload.SequenceEqual(new byte[] { 0, 1, 127, 128, 255 }) &&
                    automaticC2Read.Name == "c2 compatibility" &&
                    automaticC2Read.NullableState is null &&
                    automaticC2Read.NullablePayload is null,
                "The source-generated Native AOT C2 automatic scalar compatibility map failed.");
            Console.WriteLine("        Passed: automatic C2 scalar conversion, BSON shape, nullable values, and mapper options without manual registration.");

            Console.WriteLine("  [3.2c] Execute C2.2a explicit-ID and batch scalar writes without a manual execution map.");
            var automaticC2Writes = database.GetGeneratedCollection<AotPhaseCScalarRecord>("aot_phase_c_scalar_writes");
            var explicitC2Write = new AotPhaseCScalarRecord { Id = 900, Name = "explicit", Score = 1 };
            automaticC2Writes.Insert(41, explicitC2Write);
            var batchC2Writes = new[]
            {
                new AotPhaseCScalarRecord { Name = "batch-first", Score = 2 },
                new AotPhaseCScalarRecord { Name = "batch-second", Score = 3 }
            };
            automaticC2Writes.Insert(batchC2Writes);
            batchC2Writes[0].Score = 20;
            batchC2Writes[1].Score = 30;
            var c2BatchUpdateCount = automaticC2Writes.Update(batchC2Writes);
            var explicitC2Update = new AotPhaseCScalarRecord { Id = 999, Name = "explicit-update", Score = 40 };
            var c2ExplicitUpdate = automaticC2Writes.Update(41, explicitC2Update);
            Require(explicitC2Write.Id == 900 &&
                    automaticC2Writes.FindById(41)?.Name == "explicit-update" &&
                    explicitC2Update.Id == 999 &&
                    batchC2Writes[0].Id != 0 &&
                    batchC2Writes[1].Id != 0 &&
                    batchC2Writes[0].Id != batchC2Writes[1].Id &&
                    c2BatchUpdateCount == 2 &&
                    automaticC2Writes.FindById(batchC2Writes[0].Id)?.Score == 20 &&
                    automaticC2Writes.FindById(batchC2Writes[1].Id)?.Score == 30 &&
                    c2ExplicitUpdate,
                "The source-generated Native AOT C2.2a explicit-ID or batch scalar write failed.");
            Console.WriteLine("        Passed: automatic C2.2a explicit-ID insert/update and lazy batch insert/update without manual registration.");

            Console.WriteLine("  [3.2d] Execute C2.2b automatic-ID, batch, and explicit-ID upserts without a manual execution map.");
            var automaticC2Upserts = database.GetGeneratedCollection<AotPhaseCScalarRecord>("aot_phase_c_scalar_upserts");
            var automaticC2Upsert = new AotPhaseCScalarRecord { Name = "automatic", Score = 1 };
            var c2AutomaticInsert = automaticC2Upserts.Upsert(automaticC2Upsert);
            automaticC2Upsert.Name = "automatic-updated";
            automaticC2Upsert.Score = 2;
            var c2AutomaticUpdate = automaticC2Upserts.Upsert(automaticC2Upsert);
            var c2BatchUpserts = new[]
            {
                new AotPhaseCScalarRecord { Name = "batch-first", Score = 3 },
                new AotPhaseCScalarRecord { Name = "batch-second", Score = 4 }
            };
            var c2BatchInsertCount = automaticC2Upserts.Upsert(c2BatchUpserts);
            c2BatchUpserts[0].Score = 30;
            c2BatchUpserts[1].Score = 40;
            var c2UpsertBatchUpdateCount = automaticC2Upserts.Upsert(c2BatchUpserts);
            var explicitC2Upsert = new AotPhaseCScalarRecord { Id = 900, Name = "explicit", Score = 5 };
            var c2ExplicitInsert = automaticC2Upserts.Upsert(41, explicitC2Upsert);
            explicitC2Upsert.Name = "explicit-updated";
            explicitC2Upsert.Score = 50;
            var c2ExplicitUpsertUpdate = automaticC2Upserts.Upsert(41, explicitC2Upsert);
            Require(c2AutomaticInsert &&
                    !c2AutomaticUpdate &&
                    automaticC2Upsert.Id != 0 &&
                    automaticC2Upserts.FindById(automaticC2Upsert.Id)?.Score == 2 &&
                    c2BatchInsertCount == 2 &&
                    c2UpsertBatchUpdateCount == 0 &&
                    c2BatchUpserts[0].Id != 0 &&
                    c2BatchUpserts[1].Id != 0 &&
                    c2BatchUpserts[0].Id != c2BatchUpserts[1].Id &&
                    automaticC2Upserts.FindById(c2BatchUpserts[0].Id)?.Score == 30 &&
                    automaticC2Upserts.FindById(c2BatchUpserts[1].Id)?.Score == 40 &&
                    c2ExplicitInsert &&
                    !c2ExplicitUpsertUpdate &&
                    explicitC2Upsert.Id == 900 &&
                    automaticC2Upserts.FindById(41)?.Name == "explicit-updated",
                "The source-generated Native AOT C2.2b scalar upsert behavior failed.");
            Console.WriteLine("        Passed: automatic C2.2b scalar upserts preserve generated IDs, explicit IDs, and insert-count return semantics.");

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

        }
    }
}
