using System;
using System.Collections.Generic;
using System.Linq;
using LiteDB;

namespace LiteDB.SourceGenerator.PackageConsumer;

internal static class WideNumericScenario
{
    internal static void Run(LiteDatabase database)
    {
        var value = WideUnsigned.Maximum;
        var collection = database.GetGeneratedCollection<PackagedGeneratedRecord>("wide");
        collection.Insert(new PackagedGeneratedRecord
        {
            Id = 1, Unsigned = ulong.MaxValue, State = value,
            Fields = new Dictionary<string, object?>
            {
                ["signed"] = WideSigned.Maximum, ["unsigned"] = value, ["uint"] = WideUInt.Maximum
            }
        });
        collection.EnsureIndex(x => x.State);
        var document = database.GetCollection("wide").FindById(1);
        if (collection.Query().Select(x => x.Unsigned).Single() != ulong.MaxValue ||
            collection.Min(x => x.Unsigned) != ulong.MaxValue ||
            collection.Max(x => x.Unsigned) != ulong.MaxValue ||
            collection.FindOne(x => x.State == value)?.Id != 1 ||
            document["Fields"]["signed"].AsInt64 != long.MaxValue ||
            document["Fields"]["unsigned"].AsInt64 != -1L ||
            document["Fields"]["uint"].AsInt64 != uint.MaxValue)
        {
            throw new InvalidOperationException("Packaged wide numeric mapping lost its BSON representation.");
        }
        Console.WriteLine("[PASS] Packaged wide enum captures/dictionaries and ulong projections/aggregates preserve values.");
    }
}

public enum WideSigned : long { Maximum = long.MaxValue }
public enum WideUnsigned : ulong { Maximum = ulong.MaxValue }
public enum WideUInt : uint { Maximum = uint.MaxValue }
