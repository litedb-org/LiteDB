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

            try
            {
                using (var database = new LiteDatabase(databasePath))
                {
                    RunDocumentAndExpressionScenarios(database);
                }

                RunStreamBackedScenario();
                RunGeneratedTypedMappingScenario(databasePath);

                Console.WriteLine("LiteDB deployment smoke test passed.");
            }
            finally
            {
                File.Delete(databasePath);
            }
        }

        private static void RunDocumentAndExpressionScenarios(LiteDatabase database)
        {
            var collection = database.GetCollection("items");

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

            Require(collection.EnsureIndex("score", BsonExpression.Create("$.score")), "The Native AOT index creation failed.");

            var between = collection.Query()
                .Where("$.score BETWEEN @0 AND @1", 1, 3)
                .ToArray();
            Require(between.Length == 1 && between[0]["_id"].AsInt32 == 1, "The Native AOT BETWEEN query failed.");

            var arrayMembership = collection.Query()
                .Where("2 IN [1, 2, 3]")
                .ToArray();
            Require(arrayMembership.Length == 2, "The Native AOT array expression query failed.");

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

            Require(collection.Upsert(new BsonDocument
            {
                ["_id"] = 3,
                ["name"] = "upserted",
                ["score"] = 9
            }), "The Native AOT upsert failed.");
            Require(collection.Delete(3), "The Native AOT delete failed.");
            Require(collection.FindById(3) is null, "The Native AOT delete verification failed.");
        }

        private static void RunStreamBackedScenario()
        {
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
        }

        private static void RunGeneratedTypedMappingScenario(string databasePath)
        {
            var mapper = new BsonMapper();
            LiteDbGeneratedMappings.Register(mapper);

            using var database = new LiteDatabase(databasePath, mapper);
            var simple = database.GetGeneratedCollection<AotSimpleRecord>("aot_simple");
            simple.Insert(new AotSimpleRecord { Name = "simple", Score = 7 });

            var simpleRead = simple.FindById(1);
            Require(simpleRead?.Name == "simple" && simpleRead.Score == 7,
                "The source-generated Native AOT simple typed round trip failed.");

            var list = database.GetGeneratedCollection<AotListRecord>("aot_list");
            list.Insert(new AotListRecord
            {
                Name = "list",
                Values = ["one", "two"]
            });

            var listRead = list.FindById(1);
            Require(listRead?.Name == "list" &&
                    listRead.Values.SequenceEqual(["one", "two"]),
                "The source-generated Native AOT List<string> typed round trip failed.");
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
}
