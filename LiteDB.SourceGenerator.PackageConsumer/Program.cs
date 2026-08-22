using System;
using System.Collections.Generic;
using System.IO;
using LiteDB;
using LiteDB.Generated;

namespace LiteDB.SourceGenerator.PackageConsumer;

[BsonSourceGenerated]
public sealed class PackagedExecutionRecord
{
    public int Id { get; set; }

    public string Name { get; set; } = string.Empty;

    public int Score { get; set; }
}

[BsonSourceGenerated]
public sealed class PackagedGeneratedRecord
{
    public int Id { get; set; }

    public DateTimeOffset OccurredAt { get; set; }

    public int? Attempt { get; set; }

    public DateTimeOffset? DeliveredAt { get; set; }

    public Dictionary<string, object?> Fields { get; set; } = [];
}

internal static class Program
{
    private static int Main()
    {
        var occurredAt = new DateTimeOffset(2024, 7, 6, 8, 9, 10, TimeSpan.FromHours(5.5));
        var deliveredAt = new DateTimeOffset(2024, 7, 7, 8, 9, 10, TimeSpan.FromHours(-8));
        var mapper = new BsonMapper();

        LiteDbGeneratedMappings.Register(mapper);
        mapper.RegisterGeneratedExecutionMap(CreatePackagedExecutionMap());

        using var stream = new MemoryStream();
        using var database = new LiteDatabase(stream, mapper);
        var executionCollection = database.GetGeneratedCollection<PackagedExecutionRecord>("packaged_execution_records");
        executionCollection.Insert(new PackagedExecutionRecord { Name = "package-execution", Score = 7 });
        var executionRecord = executionCollection.FindById(1)
            ?? throw new InvalidOperationException("The packaged generated execution map did not return its scalar record.");
        Require(executionRecord.Name == "package-execution" && executionRecord.Score == 7,
            "The packaged generated execution map did not round trip its scalar record.");
        executionRecord.Name = "package-updated";
        Require(executionCollection.Update(executionRecord), "The packaged generated execution map did not update its scalar record.");
        Require(executionCollection.FindById(1)?.Name == "package-updated", "The packaged generated execution map did not read its updated scalar record.");
        Require(executionCollection.Delete(1) && executionCollection.Count() == 0,
            "The packaged generated execution map did not complete delete/count operations.");

        mapper.SerializeNullValues = true;

        var collection = database.GetGeneratedCollection<PackagedGeneratedRecord>("packaged_generated_records");
        collection.Insert(new PackagedGeneratedRecord
        {
            Id = 17,
            OccurredAt = occurredAt,
            Attempt = 3,
            DeliveredAt = deliveredAt,
            Fields = new Dictionary<string, object?>
            {
                ["message"] = "package-consumer",
                ["attempt"] = 3,
                ["missing"] = null,
                ["nested"] = new Dictionary<string, object?>
                {
                    ["enabled"] = true
                }
            }
        });

        var actual = collection.FindById(17);

        Require(actual is not null, "The packaged generated collection did not return the inserted record.");
        Require(actual!.OccurredAt.EqualsExact(occurredAt), "The packaged generated DateTimeOffset mapping changed ticks or offset.");
        Require(actual.Attempt == 3, "The packaged generated nullable scalar mapping did not preserve the populated value.");
        Require(actual.DeliveredAt.HasValue && actual.DeliveredAt.Value.EqualsExact(deliveredAt), "The packaged generated nullable DateTimeOffset mapping changed ticks or offset.");
        var fields = actual.Fields ?? throw new InvalidOperationException("The packaged generated dynamic dictionary was null.");
        Require((string)fields["message"]! == "package-consumer", "The packaged generated dynamic dictionary string value did not round trip.");
        Require((int)fields["attempt"]! == 3, "The packaged generated dynamic dictionary numeric value did not round trip.");
        Require(fields["missing"] is null, "The packaged generated dynamic dictionary null value did not round trip.");
        Require(fields["nested"] is Dictionary<string, object?> nested && (bool)nested["enabled"]!, "The packaged generated nested dynamic dictionary did not round trip.");

        Console.WriteLine("[PASS] Packaged LiteDB.SourceGenerator restore, generation, registration, Native AOT publish, and real LiteDB round trip succeeded.");
        return 0;
    }

    private static GeneratedEntityMap<PackagedExecutionRecord> CreatePackagedExecutionMap()
    {
        return new GeneratedEntityMap<PackagedExecutionRecord>(
            record => new BsonDocument
            {
                ["_id"] = record.Id,
                [nameof(PackagedExecutionRecord.Name)] = record.Name,
                [nameof(PackagedExecutionRecord.Score)] = record.Score
            },
            document => new PackagedExecutionRecord
            {
                Id = document["_id"].AsInt32,
                Name = document[nameof(PackagedExecutionRecord.Name)].AsString,
                Score = document[nameof(PackagedExecutionRecord.Score)].AsInt32
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
