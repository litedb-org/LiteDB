using System;
using System.IO;
using System.Linq;

using LiteDB;

var databasePath = Path.Combine(Path.GetTempPath(), $"litedb-aot-{Guid.NewGuid():N}.db");

try
{
    using (var database = new LiteDatabase(databasePath))
    {
        RunDocumentAndExpressionScenarios(database);
    }

    RunStreamBackedScenario();

    Console.WriteLine("LiteDB deployment smoke test passed.");
}
finally
{
    File.Delete(databasePath);
}

static void RunDocumentAndExpressionScenarios(LiteDatabase database)
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
    Require(item is not null && item["name"].AsString == "native-aot", "The Native AOT database round trip failed.");

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
    Require(updated is not null &&
            updated["name"].AsString == "NATIVE-AOT" &&
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

static void RunStreamBackedScenario()
{
    using (var stream = new MemoryStream())
    using (var database = new LiteDatabase(stream))
    {
        var collection = database.GetCollection("streamitems");
        collection.Insert(new BsonDocument
        {
            ["_id"] = 1,
            ["name"] = "stream-backed"
        });

        var item = collection.FindById(1);
        Require(item is not null && item["name"].AsString == "stream-backed", "The Native AOT stream-backed database round trip failed.");
    }
}

static void Require(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException(message);
    }
}
