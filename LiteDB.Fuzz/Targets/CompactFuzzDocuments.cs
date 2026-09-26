using LiteDB.Vector;

namespace LiteDB.Fuzz.Targets;

internal static class CompactFuzzDocuments
{
    internal static BsonDocument Create(int id, Random random, int shape)
    {
        var document = new BsonDocument
        {
            ["_id"] = id,
            ["Kind"] = shape % 6
        };

        switch (shape % 6)
        {
            case 0:
                AddStableFields(document, random, 16);
                break;
            case 1:
                for (var i = 0; i < 20; i++)
                {
                    if (random.Next(4) != 0) document[$"OptionalRepeatedProperty{i}"] = Value(random, 0);
                }
                break;
            case 2:
                document["NestedDocumentWithLongPropertyNames"] = Nested(random, 0);
                document["SecondNestedDocument"] = Nested(random, 0);
                break;
            case 3:
                document["ArrayWithNoNumericBsonKeys"] = Array(random, random.Next(4, 40), 0);
                document["SecondArray"] = Array(random, random.Next(0, 12), 0);
                break;
            case 4:
                AddEveryType(document, random);
                break;
            default:
                var suffix = random.Next(1_000_000);
                for (var i = 0; i < random.Next(5, 18); i++)
                    document[$"Dynamic_{suffix}_{i}_{random.Next(1000)}"] = Value(random, 0);
                break;
        }

        return document;
    }

    internal static BsonDocument Fixed(int id)
    {
        var document = new BsonDocument { ["_id"] = id, ["Kind"] = 0 };
        for (var i = 0; i < 16; i++) document[$"RepeatedPropertyName{i}"] = id + i;
        return document;
    }

    internal static BsonDocument Normalize(BsonDocument document, bool utcDate = false) =>
        BsonSerializer.Deserialize(BsonSerializer.Serialize(document), utcDate);

    internal static bool Equal(BsonDocument left, BsonDocument right) =>
        BsonSerializer.Serialize(left).SequenceEqual(BsonSerializer.Serialize(right));

    private static void AddStableFields(BsonDocument document, Random random, int count)
    {
        for (var i = 0; i < count; i++) document[$"RepeatedPropertyName{i}"] = Value(random, 0);
    }

    private static void AddEveryType(BsonDocument document, Random random)
    {
        document["NullValue"] = BsonValue.Null;
        document["MinimumValue"] = BsonValue.MinValue;
        document["MaximumValue"] = BsonValue.MaxValue;
        document["IntegerValue"] = random.Next();
        document["LongValue"] = ((long)random.Next() << 32) | (uint)random.Next();
        document["DoubleValue"] = random.NextDouble() * random.Next(-1000, 1001);
        document["DecimalValue"] = random.Next(-100000, 100001) / 100m;
        document["StringValue"] = Text(random);
        document["BinaryValue"] = Bytes(random, random.Next(0, 80));
        document["GuidValue"] = new Guid(Bytes(random, 16));
        document["ObjectIdValue"] = new ObjectId(Bytes(random, 12));
        document["BooleanValue"] = random.Next(2) == 0;
        document["DateValue"] = DateTime.UnixEpoch.AddMinutes(random.Next(-1_000_000, 1_000_001));
        document["VectorValue"] = new BsonVector(Enumerable.Range(0, random.Next(1, 12))
            .Select(_ => (float)(random.NextDouble() * 20 - 10)).ToArray());
        document["DocumentValue"] = Nested(random, 0);
        document["ArrayValue"] = Array(random, random.Next(0, 16), 0);
    }

    private static BsonDocument Nested(Random random, int depth)
    {
        var document = new BsonDocument();
        var count = random.Next(1, 8);
        for (var i = 0; i < count; i++) document[$"NestedLongProperty{i}"] = Value(random, depth + 1);
        return document;
    }

    private static BsonArray Array(Random random, int count, int depth)
    {
        var result = new BsonArray();
        for (var i = 0; i < count; i++) result.Add(Value(random, depth + 1));
        return result;
    }

    private static BsonValue Value(Random random, int depth)
    {
        var kind = random.Next(depth >= 3 ? 12 : 14);
        return kind switch
        {
            0 => BsonValue.Null,
            1 => random.Next(int.MinValue, int.MaxValue),
            2 => ((long)random.Next() << 32) | (uint)random.Next(),
            3 => random.NextDouble() * random.Next(-10000, 10001),
            4 => random.Next(-100000, 100001) / 100m,
            5 => Text(random),
            6 => Bytes(random, random.Next(0, 128)),
            7 => new Guid(Bytes(random, 16)),
            8 => new ObjectId(Bytes(random, 12)),
            9 => random.Next(2) == 0,
            10 => DateTime.UnixEpoch.AddSeconds(random.Next(-10_000_000, 10_000_001)),
            11 => random.Next(2) == 0 ? BsonValue.MinValue : BsonValue.MaxValue,
            12 => Nested(random, depth),
            _ => Array(random, random.Next(0, 12), depth)
        };
    }

    private static string Text(Random random)
    {
        var alphabet = new[] { "alpha", "héllo", "世界", "emoji🙂", "with\0nul", "", "ßeta" };
        return alphabet[random.Next(alphabet.Length)] + new string((char)('a' + random.Next(26)), random.Next(0, 40));
    }

    private static byte[] Bytes(Random random, int count)
    {
        var bytes = new byte[count];
        random.NextBytes(bytes);
        return bytes;
    }
}
