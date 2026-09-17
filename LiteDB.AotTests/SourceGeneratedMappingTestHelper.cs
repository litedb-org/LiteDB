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

    public static GeneratedEntityMap<PhaseBGeneratedRecord> CreatePhaseBExecutionMap()
    {
        return new GeneratedEntityMap<PhaseBGeneratedRecord>(
            record => new BsonDocument
            {
                ["_id"] = record.Id,
                [nameof(PhaseBGeneratedRecord.Name)] = record.Name,
                [nameof(PhaseBGeneratedRecord.Score)] = record.Score
            },
            document => new PhaseBGeneratedRecord
            {
                Id = document["_id"].AsInt32,
                Name = document[nameof(PhaseBGeneratedRecord.Name)].AsString,
                Score = document[nameof(PhaseBGeneratedRecord.Score)].AsInt64
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
