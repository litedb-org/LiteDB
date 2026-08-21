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
            Console.WriteLine("  [3.1] Register generated mappings and round-trip a scalar typed record.");
            var mapper = new BsonMapper { SerializeNullValues = true };
            LiteDbGeneratedMappings.Register(mapper);

            using var database = new LiteDatabase(databasePath, mapper);
            var simple = database.GetGeneratedCollection<AotSimpleRecord>("aot_simple");
            simple.Insert(new AotSimpleRecord { Name = "simple", Score = 7 });

            var simpleRead = simple.FindById(1);
            Require(simpleRead?.Name == "simple" && simpleRead.Score == 7,
                "The source-generated Native AOT simple typed round trip failed.");
            Console.WriteLine("        Passed: generated mapper registration and scalar typed round trip.");

            Console.WriteLine("  [3.2] Update, count, and delete a generated typed record.");
            simpleRead.Name = "updated";
            Require(simple.Update(simpleRead), "The source-generated Native AOT typed update failed.");
            Require(simple.FindById(1)?.Name == "updated" && simple.Count() == 1,
                "The source-generated Native AOT typed update verification failed.");
            Require(simple.Delete(1) && simple.Count() == 0,
                "The source-generated Native AOT typed delete failed.");
            Console.WriteLine("        Passed: generated typed update, count, and delete.");

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
        }

        private static void RunScenario(string name, Action scenario)
        {
            Console.WriteLine($"[SCENARIO] {name}");
            scenario();
            Console.WriteLine($"[PASS] {name}");
            Console.WriteLine();
        }

        private static void RequireNativeScalar(string field, bool condition, string expected, string actual)
        {
            Require(condition,
                $"The source-generated Native AOT native scalar round trip failed for '{field}'. Expected: '{expected}'. Actual: '{actual}'.");
            Console.WriteLine($"        Passed: {field}.");
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
    public sealed class AotSecondaryRecord
    {
        public int Id { get; set; }
        public string Description { get; set; } = string.Empty;
    }
}
