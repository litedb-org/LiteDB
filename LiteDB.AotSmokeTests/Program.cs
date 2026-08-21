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
        }

        private static void RunScenario(string name, Action scenario)
        {
            Console.WriteLine($"[SCENARIO] {name}");
            scenario();
            Console.WriteLine($"[PASS] {name}");
            Console.WriteLine();
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
