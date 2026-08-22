using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using LiteDB;
using LiteDB.Generated;

namespace LiteDB.SourceGenerator.PackageConsumer;

[BsonSourceGenerated]
public sealed class PackagedExecutionRecord
{
    public int Id { get; set; }

    public string Name { get; set; } = string.Empty;

    public long Score { get; set; }

    // Keeps this retained manual Phase B package fixture outside C2 automatic scalar-map emission.
    public DateTimeOffset LegacyProbe { get; set; }
}

[BsonSourceGenerated]
public sealed class PackagedC1ScalarRecord
{
    public int Id { get; set; }

    public string? Name { get; set; }

    public int Score { get; set; }
}

[BsonSourceGenerated]
public sealed class PackagedC2ScalarRecord
{
    public int Id { get; set; }
    public bool BooleanValue { get; set; }
    public uint UnsignedInteger { get; set; }
    public long SignedLong { get; set; }
    public ulong UnsignedLong { get; set; }
    public decimal DecimalValue { get; set; }
    public PackagedScalarState State { get; set; }
    public DateTime Timestamp { get; set; }
    public ObjectId ObjectId { get; set; } = ObjectId.Empty;
    public Guid CorrelationId { get; set; }
    public byte[] Payload { get; set; } = [];
    public string? Name { get; set; }
    public PackagedScalarState? NullableState { get; set; }
    public byte[]? NullablePayload { get; set; }
}

public enum PackagedScalarState
{
    Ready = 1,
    Completed = 5
}

[BsonSourceGenerated]
public sealed record PackagedMutableRecord
{
    public int Id { get; set; }

    public string Name { get; set; } = string.Empty;
}

public class PackagedOverrideBase
{
    [BsonId(false)]
    public virtual int OverrideId { get; set; }
}

public class PackagedOverrideMiddle : PackagedOverrideBase
{
    public override int OverrideId { get; set; }
}

[BsonSourceGenerated]
public sealed class PackagedOverrideRecord : PackagedOverrideMiddle
{
    private int _overrideId;

    public override int OverrideId
    {
        get => _overrideId;
        set
        {
            _overrideId = value;
            SetterCalls++;
        }
    }

    public string Name { get; set; } = string.Empty;

    [BsonIgnore]
    public int SetterCalls { get; private set; }
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
        executionCollection.Insert(new PackagedExecutionRecord { Name = "package-execution", Score = 7L });
        var executionRecord = executionCollection.FindById(1)
            ?? throw new InvalidOperationException("The packaged generated execution map did not return its scalar record.");
        Require(executionRecord.Name == "package-execution" && executionRecord.Score == 7L,
            "The packaged generated execution map did not round trip its scalar record.");
        executionRecord.Name = "package-updated";
        Require(executionCollection.Update(executionRecord), "The packaged generated execution map did not update its scalar record.");
        Require(executionCollection.FindById(1)?.Name == "package-updated", "The packaged generated execution map did not read its updated scalar record.");
        Require(executionCollection.Delete(1) && executionCollection.Count() == 0,
            "The packaged generated execution map did not complete delete/count operations.");

        var automaticC1Collection = database.GetGeneratedCollection<PackagedC1ScalarRecord>("packaged_c1_scalar_records");
        automaticC1Collection.Insert(new PackagedC1ScalarRecord { Score = 8 });
        var automaticC1Record = automaticC1Collection.FindById(1);
        Require(automaticC1Record is not null &&
                automaticC1Record.Id == 1 &&
                automaticC1Record.Name is null &&
                automaticC1Record.Score == 8,
            "The packaged C1 automatic execution map did not round trip its scalar record.");
        Require(automaticC1Collection.Update(new PackagedC1ScalarRecord { Id = 1, Name = "automatic", Score = 9 }) &&
                automaticC1Collection.FindById(1)?.Name == "automatic" &&
                automaticC1Collection.Count() == 1 &&
                automaticC1Collection.Delete(1),
            "The packaged C1 automatic execution map did not complete direct scalar CRUD.");

        var expectedC2ObjectId = new ObjectId("64c61e5f18a9421a8862c71c");
        var expectedC2CorrelationId = new Guid("d29368bb-9669-4f84-9384-c8eb15caa0a8");
        var automaticC2Collection = database.GetGeneratedCollection<PackagedC2ScalarRecord>("packaged_c2_scalar_records");
        automaticC2Collection.Insert(new PackagedC2ScalarRecord
        {
            Id = 1,
            BooleanValue = true,
            UnsignedInteger = uint.MaxValue,
            SignedLong = -9_000_000_000L,
            UnsignedLong = ulong.MaxValue,
            DecimalValue = 7.75m,
            State = PackagedScalarState.Completed,
            Timestamp = new DateTime(2024, 8, 1, 12, 34, 56, 789, DateTimeKind.Utc),
            ObjectId = expectedC2ObjectId,
            CorrelationId = expectedC2CorrelationId,
            Payload = [0, 1, 127, 128, 255],
            Name = "  package c2  ",
            NullableState = null,
            NullablePayload = null
        });
        var automaticC2Document = database.GetCollection("packaged_c2_scalar_records").FindById(1);
        var automaticC2Record = automaticC2Collection.FindById(1);
        Require(automaticC2Document[nameof(PackagedC2ScalarRecord.UnsignedInteger)].Type == BsonType.Int64 &&
                automaticC2Document[nameof(PackagedC2ScalarRecord.State)].Type == BsonType.String &&
                automaticC2Document[nameof(PackagedC2ScalarRecord.State)].AsString == nameof(PackagedScalarState.Completed) &&
                automaticC2Document[nameof(PackagedC2ScalarRecord.NullableState)].IsNull &&
                automaticC2Document[nameof(PackagedC2ScalarRecord.NullablePayload)].IsNull &&
                automaticC2Record is not null &&
                automaticC2Record.BooleanValue &&
                automaticC2Record.UnsignedInteger == uint.MaxValue &&
                automaticC2Record.SignedLong == -9_000_000_000L &&
                automaticC2Record.UnsignedLong == ulong.MaxValue &&
                automaticC2Record.DecimalValue == 7.75m &&
                automaticC2Record.State == PackagedScalarState.Completed &&
                automaticC2Record.ObjectId == expectedC2ObjectId &&
                automaticC2Record.CorrelationId == expectedC2CorrelationId &&
                automaticC2Record.Payload.SequenceEqual(new byte[] { 0, 1, 127, 128, 255 }) &&
                automaticC2Record.Name == "package c2" &&
                automaticC2Record.NullableState is null &&
                automaticC2Record.NullablePayload is null,
            "The packaged C2 automatic scalar compatibility map did not round trip without a manual execution map.");

        var mutableRecords = database.GetGeneratedCollection<PackagedMutableRecord>("packaged_mutable_records");
        mutableRecords.Insert(new PackagedMutableRecord { Name = "record" });
        Require(mutableRecords.FindById(1)?.Name == "record",
            "The packaged generated mutable record did not round trip.");

        var overrideRecords = database.GetGeneratedCollection<PackagedOverrideRecord>("packaged_override_records");
        overrideRecords.Insert(new PackagedOverrideRecord { OverrideId = 9, Name = "override" });
        var overrideRecord = overrideRecords.FindById(9);
        Require(overrideRecord is not null &&
                overrideRecord.OverrideId == 9 &&
                overrideRecord.SetterCalls == 1 &&
                overrideRecord.Name == "override",
            "The packaged generated virtual override did not materialize through the most-derived setter.");

        mapper.SerializeNullValues = true;

        var automaticC1Nulls = database.GetGeneratedCollection<PackagedC1ScalarRecord>("packaged_c1_scalar_nulls");
        automaticC1Nulls.Insert(new PackagedC1ScalarRecord { Score = 10 });
        var automaticC1NullDocument = database.GetCollection("packaged_c1_scalar_nulls").FindById(1);
        Require(automaticC1NullDocument[nameof(PackagedC1ScalarRecord.Name)].IsNull &&
                automaticC1Nulls.FindById(1)?.Name is null,
            "The packaged C1 automatic execution map did not persist a configured null string.");

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

        var legacyDateTimeOffset = new DateTimeOffset(2024, 7, 8, 8, 9, 10, TimeSpan.FromHours(5.5)).AddTicks(4321);
        database.GetCollection("packaged_generated_records").Insert(new BsonDocument
        {
            ["_id"] = 18,
            [nameof(PackagedGeneratedRecord.OccurredAt)] = legacyDateTimeOffset.UtcDateTime
        });
        var legacyRead = collection.FindById(18);
        var expectedLegacyTicks = legacyDateTimeOffset.UtcDateTime.Ticks - (legacyDateTimeOffset.UtcDateTime.Ticks % TimeSpan.TicksPerMillisecond);
        Require(legacyRead is not null &&
                legacyRead.OccurredAt.UtcDateTime.Ticks == expectedLegacyTicks &&
                legacyRead.OccurredAt.Offset == TimeSpan.Zero,
            "The packaged generated DateTimeOffset mapping did not read the legacy BSON DateTime representation.");

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
                Score = document[nameof(PackagedExecutionRecord.Score)].AsInt64
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
