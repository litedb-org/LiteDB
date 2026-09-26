using System;
using System.IO;

using LiteDB.Generated;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LiteDB.AotTests;

internal static class SourceGeneratedMappingTestHelper
{
    public static void AssertCanonicalDateTimeOffsetValue(BsonValue value, DateTimeOffset expected)
    {
        Assert.IsTrue(value.IsDateTime);
        Assert.AreEqual(GetCanonicalDateTimeOffsetTicks(expected), value.AsDateTime.ToUniversalTime().Ticks);
    }

    public static void AssertCanonicalDateTimeOffset(DateTimeOffset expected, DateTimeOffset actual)
    {
        Assert.AreEqual(GetCanonicalDateTimeOffsetTicks(expected), actual.UtcTicks);
        Assert.AreEqual(TimeSpan.Zero, actual.Offset);
    }

    public static BsonMapper CreateGeneratedMapper()
    {
        var mapper = new BsonMapper();
        LiteDbGeneratedMappings.Register(mapper);
        return mapper;
    }

    public static GeneratedEntityMap<ManualGeneratedRecord> CreateManualExecutionMap()
    {
        return new GeneratedEntityMap<ManualGeneratedRecord>(
            (record, _) => new BsonDocument
            {
                ["_id"] = record.Id,
                [nameof(ManualGeneratedRecord.Name)] = record.Name,
                [nameof(ManualGeneratedRecord.Score)] = record.Score
            },
            (document, _) => new ManualGeneratedRecord
            {
                Id = document["_id"].AsInt32,
                Name = document[nameof(ManualGeneratedRecord.Name)].AsString,
                Score = document[nameof(ManualGeneratedRecord.Score)].AsInt64
            });
    }

    public static string GetDatabasePath()
    {
        return Path.Combine(Path.GetTempPath(), $"litedb-source-generated-test-{Guid.NewGuid():N}.db");
    }

    private static long GetCanonicalDateTimeOffsetTicks(DateTimeOffset value) =>
        value == DateTimeOffset.MinValue || value == DateTimeOffset.MaxValue
            ? DateTime.SpecifyKind(value.UtcDateTime, DateTimeKind.Unspecified).ToUniversalTime().Ticks
            : value.UtcTicks - (value.UtcTicks % TimeSpan.TicksPerMillisecond);
}
