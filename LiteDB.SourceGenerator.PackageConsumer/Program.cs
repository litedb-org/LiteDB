using System;
using System.Collections.Generic;
using System.IO;
using LiteDB;
using LiteDB.Generated;

namespace LiteDB.SourceGenerator.PackageConsumer;

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
        var mapper = new BsonMapper { SerializeNullValues = true };

        LiteDbGeneratedMappings.Register(mapper);

        using var stream = new MemoryStream();
        using var database = new LiteDatabase(stream, mapper);
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

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
