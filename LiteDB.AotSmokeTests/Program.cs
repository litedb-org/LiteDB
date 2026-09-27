using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

using LiteDB.Generated;
using static LiteDB.AotSmokeTests.SmokeAssert;

#nullable enable
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
                RunScenario("1/6 Document and expression operations", () =>
                {
                    using var database = new LiteDatabase(databasePath);
                    RunDocumentAndExpressionScenarios(database);
                });
                RunScenario("2/6 Stream-backed database round trip", RunStreamBackedScenario);
                RunScenario("3/6 Source-generated typed mappings", () => RunGeneratedTypedMappingScenario(databasePath));
                RunScenario("4/6 Engine features through the document API", EngineScenarios.Run);
                RunScenario("5/6 Expression engine sweep", ExpressionSweepScenarios.Run);
                RunScenario("6/6 SQL statement sweep", SqlSweepScenarios.Run);

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

        [System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage(
            "Trimming",
            "IL2026",
            Justification = "The source generator emits direct access to every member used by these expression trees, so their accessors remain rooted.")]
        private static void RunGeneratedQueryParity(LiteDatabase database)
        {
            Console.WriteLine("  [3.1r] Execute generated fluent, aggregate, and bulk mutation parity APIs.");
            var parity = database.GetGeneratedCollection<AotSimpleRecord>("aot_query_parity");
            parity.Insert(new[]
            {
                new AotSimpleRecord { Name = "low", Score = 1 },
                new AotSimpleRecord { Name = "high", Score = 2 }
            });

            var scores = parity.Query()
                .Where(record => record.Score >= 1)
                .OrderByDescending(record => record.Score)
                .Select(record => record.Score)
                .ToArray();
            var groups = parity.Query()
                .GroupBy(record => record.Score >= 2)
                .ToArray();
            var groupKeys = parity.Query()
                .GroupBy(record => record.Score >= 2)
                .Select(group => group.Key)
                .ToArray();
            var entities = parity.Query()
                .Select(record => new AotSimpleRecord
                {
                    Name = record.Name,
                    Score = record.Score + 1
                })
                .ToArray();

            Require(scores.SequenceEqual(new long[] { 2, 1 }) &&
                    groups.Length == 2 &&
                    groups.Single(group => group.Key).Count() == 1 &&
                    groupKeys.OrderBy(key => key).SequenceEqual(new[] { false, true }) &&
                    entities.Select(record => record.Score).OrderBy(score => score).SequenceEqual(new long[] { 2, 3 }) &&
                    parity.Count(record => record.Score >= 1) == 2 &&
                    parity.Min(record => record.Score) == 1 &&
                    parity.Max(record => record.Score) == 2 &&
                    parity.UpdateMany(
                        record => new AotSimpleRecord { Score = record.Score + 10 },
                        record => record.Score == 2) == 1 &&
                    parity.DeleteMany(record => record.Score == 12) == 1 &&
                    parity.DeleteAll() == 1,
                "The generated Native AOT parity operations failed.");
            Console.WriteLine("        Passed: generated LINQ, grouping, projections, aggregates, update-many, and deletion APIs.");
        }

        private static void RunGeneratedTypedMappingScenario(string databasePath)
        {
            Console.WriteLine("  [3.1] Automatically register generated execution maps and round-trip an inherited scalar typed record.");
            var mapper = new FailOnGenericConversionMapper();
            LiteDbGeneratedMappings.Register(mapper);

            using var database = new LiteDatabase(databasePath, mapper);
            var simple = database.GetGeneratedCollection<AotSimpleRecord>("aot_simple");
            simple.Insert(new AotSimpleRecord { Name = "simple", Score = 7 });

            var simpleRead = simple.FindById(1);
            Require(simpleRead?.Name == "simple" && simpleRead.Score == 7,
                "The source-generated Native AOT simple typed round trip failed.");
            Console.WriteLine("        Passed: automatic inherited execution-map registration and scalar typed round trip.");

            Console.WriteLine("  [3.1q] Query generated records through the generated deserializer.");
            var queryRead = simple.Query()
                .Where(BsonExpression.Create("Score = 7"))
                .FirstOrDefault();
            Require(queryRead?.Name == "simple" && queryRead.Score == 7,
                "The source-generated Native AOT query did not use the generated deserializer.");
            Console.WriteLine("        Passed: generated query filtering and typed materialization.");

            RunGeneratedQueryParity(database);

            Console.WriteLine("  [3.1a] Automatically register and execute a generated scalar map.");
            var automatic = database.GetGeneratedCollection<AotGeneratedScalarRecord>("aot_generated_scalar");
            automatic.Insert(new AotGeneratedScalarRecord { Score = 8 });
            var automaticRead = automatic.FindById(1);
            Require(automaticRead is not null && automaticRead.Id == 1 && automaticRead.Name is null && automaticRead.Score == 8,
            "The source-generated Native AOT scalar execution map failed.");
            Require(automatic.Update(new AotGeneratedScalarRecord { Id = 1, Name = "automatic", Score = 9 }) &&
            automatic.FindById(1)?.Name == "automatic" &&
            automatic.Count() == 1 &&
            automatic.Delete(1),
            "The source-generated Native AOT scalar execution-map CRUD failed.");
            Console.WriteLine("        Passed: automatic execution-map registration and direct scalar CRUD without manual registration.");

            Console.WriteLine("  [3.1b] Evaluate a static-member LINQ expression through the generated mapper.");
            // DateTime.Today is a static member the visitor has to evaluate itself, not translate.
            Require(simple.Count(record => record.Score < DateTime.Today.Year) == 1,
                "The source-generated Native AOT static-member LINQ expression evaluation failed.");
            Console.WriteLine("        Passed: static-member LINQ expression evaluation without runtime code generation.");

            Console.WriteLine("  [3.2] Update, count, and delete a generated typed record.");
            simpleRead!.Name = "updated";
            Require(simple.Update(simpleRead), "The source-generated Native AOT typed update failed.");
            Require(simple.FindById(1)?.Name == "updated" && simple.Count() == 1,
                "The source-generated Native AOT typed update verification failed.");
            Require(simple.Delete(1) && simple.Count() == 0,
                "The source-generated Native AOT typed delete failed.");
            Console.WriteLine("        Passed: generated typed update, count, and delete.");

            mapper.SerializeNullValues = true;

            Console.WriteLine("  [3.2a] Persist a null string through the generated scalar map.");
            var automaticNulls = database.GetGeneratedCollection<AotGeneratedScalarRecord>("aot_generated_scalar_nulls");
            automaticNulls.Insert(new AotGeneratedScalarRecord { Score = 10 });
            var automaticNullDocument = database.GetCollection("aot_generated_scalar_nulls").FindById(1);
            Require(automaticNullDocument[nameof(AotGeneratedScalarRecord.Name)].IsNull &&
                    automaticNulls.FindById(1)?.Name is null,
                "The source-generated Native AOT map did not persist a configured null string.");
            Console.WriteLine("        Passed: automatic execution map persisted and materialized configured BSON null.");

            GeneratedScalarWriteScenarios.Run(database);
            GeneratedValueScenarios.Run(database);
            GeneratedLinqScenarios.Run(database);
        }
    }
}
