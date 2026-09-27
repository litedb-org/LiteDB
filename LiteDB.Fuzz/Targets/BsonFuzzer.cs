using LiteDB.Engine;

namespace LiteDB.Fuzz.Targets;

internal sealed class BsonFuzzer : IFuzzTarget
{
    public string Name => "bson";
    public string Description => "Nested BSON round trips through contiguous and adversarially fragmented buffers.";

    public Task RunAsync(FuzzContext context)
    {
        var malformedRejected = 0;
        while (context.Next())
        {
            var document = Document(context.Random, 0);
            var canonical = BsonSerializer.Serialize(document);
            var contiguous = BsonSerializer.Deserialize(canonical);
            Equal(context, canonical, contiguous, "contiguous");

            foreach (var slices in Segmentations(canonical, context.Random))
            {
                using var reader = new BufferReader(slices);
                var fragmented = reader.ReadDocument().GetValue();
                Equal(context, canonical, fragmented, "fragmented read");
            }

            var output = new byte[canonical.Length];
            var writeSlices = RandomSlices(output, context.Random, includeSingleBytes: context.Steps % 7 == 0);
            using (var writer = new BufferWriter(writeSlices)) writer.WriteDocument(document, true);
            context.Check(output.SequenceEqual(canonical), "Fragmented writer differed from contiguous serialization.");

            if (canonical.Length > 5)
            {
                var truncated = canonical.Take(context.Random.Next(1, canonical.Length)).ToArray();
                Reject(context, truncated, canonical, "truncated BSON");
                malformedRejected++;

                var badLength = canonical.ToArray();
                BitConverter.GetBytes(canonical.Length + context.Random.Next(1, 4096)).CopyTo(badLength, 0);
                Reject(context, badLength, canonical, "oversized BSON length");
                malformedRejected++;

                var mutated = canonical.ToArray();
                var position = context.Random.Next(4, mutated.Length);
                mutated[position] ^= (byte)(1 << context.Random.Next(8));
                if (!TryReadMutation(context, mutated)) malformedRejected++;
                AssertUsable(context, canonical);
            }
            context.Trace("bson", new { bytes = canonical.Length, depth = 4 });
        }
        context.Metrics["malformedInputsRejected"] = malformedRejected;
        return Task.CompletedTask;
    }

    private static BsonDocument Document(Random random, int depth)
    {
        var document = new BsonDocument();
        var count = random.Next(1, 7);
        for (var i = 0; i < count; i++) document[Key(random, i)] = Value(random, depth);
        return document;
    }

    private static BsonValue Value(Random random, int depth)
    {
        var limit = depth >= 4 ? 12 : 16;
        return random.Next(limit) switch
        {
            0 => BsonValue.Null,
            1 => random.Next(int.MinValue, int.MaxValue),
            2 => ((long)random.Next() << 32) | (uint)random.Next(),
            3 => BitConverter.Int64BitsToDouble(((long)random.Next() << 32) | (uint)random.Next()),
            4 => new decimal(random.Next(), random.Next(), random.Next(), random.Next(2) == 0, (byte)random.Next(29)),
            5 => Text(random, random.Next(0, 600)),
            6 => Bytes(random, random.Next(0, 600)),
            7 => new ObjectId(Bytes(random, 12)),
            8 => new Guid(Bytes(random, 16)),
            9 => random.Next(2) == 0,
            10 => new DateTime(random.NextInt64(DateTime.MinValue.Ticks, DateTime.MaxValue.Ticks), DateTimeKind.Utc),
            11 => new BsonVector(Enumerable.Range(0, random.Next(1, 20)).Select(_ => (float)(random.NextDouble() * 2 - 1)).ToArray()),
            12 => Document(random, depth + 1),
            13 => new BsonArray(Enumerable.Range(0, random.Next(0, 7)).Select(_ => Value(random, depth + 1))),
            14 => BsonValue.MinValue,
            _ => BsonValue.MaxValue
        };
    }

    private static string Key(Random random, int index) => index switch
    {
        0 => "", 1 => new string('k', random.Next(250, 520)), 2 => "nul-key", _ => Text(random, random.Next(1, 40)).Replace('\0', '_')
    };

    private static string Text(Random random, int length)
    {
        var alphabet = new[] { 'a', 'Z', '\0', 'é', '\u0301', 'ı', '中' };
        return new string(Enumerable.Range(0, length).Select(_ => alphabet[random.Next(alphabet.Length)]).ToArray());
    }

    private static byte[] Bytes(Random random, int length)
    {
        var bytes = new byte[length];
        random.NextBytes(bytes);
        return bytes;
    }

    private static IEnumerable<IEnumerable<BufferSlice>> Segmentations(byte[] bytes, Random random)
    {
        yield return RandomSlices(bytes, random, false);
        if (bytes.Length <= 4096) yield return RandomSlices(bytes, random, true);
        foreach (var split in new[] { 1, 2, 3, 4, 5, bytes.Length / 2, bytes.Length - 1 }.Where(x => x > 0 && x < bytes.Length).Distinct())
            yield return new[] { new BufferSlice(bytes, 0, split), new BufferSlice(bytes, split, bytes.Length - split) };
    }

    private static BufferSlice[] RandomSlices(byte[] bytes, Random random, bool includeSingleBytes)
    {
        var slices = new List<BufferSlice>();
        var position = 0;
        while (position < bytes.Length)
        {
            var length = includeSingleBytes ? 1 : Math.Min(bytes.Length - position, random.Next(1, Math.Min(64, bytes.Length - position) + 1));
            slices.Add(new BufferSlice(bytes, position, length));
            position += length;
        }
        return slices.ToArray();
    }

    private static void Reject(FuzzContext context, byte[] bytes, byte[] canonical, string mode)
    {
        context.Check(!TryReadMutation(context, bytes), $"Known-invalid {mode} was accepted.");
        AssertUsable(context, canonical);
    }

    private static bool TryReadMutation(FuzzContext context, byte[] bytes)
    {
        try
        {
            using var reader = new BufferReader(bytes);
            var value = reader.ReadDocument().GetValue();
            var encoded = BsonSerializer.Serialize(value);
            _ = BsonSerializer.Deserialize(encoded);
            return true;
        }
        catch (Exception error) when (error is LiteException or ArgumentException or InvalidDataException or NotSupportedException)
        {
            return false;
        }
        catch (Exception error)
        {
            var path = Path.Combine(context.DirectoryPath, $"unexpected-bson-step-{context.Steps}.bin");
            File.WriteAllBytes(path, bytes);
            context.RegisterFile(path);
            throw new FuzzFailureException("BSON_INTERNAL_MALFORMED_FAILURE",
                $"Malformed BSON escaped through internal exception {error.GetType().FullName}: {error}. " +
                $"Input saved to {Path.GetFileName(path)}.");
        }
    }

    private static void AssertUsable(FuzzContext context, byte[] canonical)
    {
        var decoded = BsonSerializer.Deserialize(canonical);
        context.Check(BsonSerializer.Serialize(decoded).SequenceEqual(canonical),
            "A malformed BSON rejection poisoned the following valid read.");
    }

    private static void Equal(FuzzContext context, byte[] canonical, BsonDocument actual, string mode) =>
        context.Check(BsonSerializer.Serialize(actual).SequenceEqual(canonical), $"BSON {mode} round trip changed the document.");
}
