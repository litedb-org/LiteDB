using System.Buffers.Binary;

using LiteDB.Engine;

namespace LiteDB.Fuzz.Targets;

internal sealed class CompactCodecFuzzer : IFuzzTarget
{
    public string Name => "compact-codec";
    public string Description => "Compact codec round trips, schemas, projections, nested values, and structural mutations.";

    public Task RunAsync(FuzzContext context)
    {
        var catalog = new SchemaCatalog();
        var decoded = 0;
        var projected = 0;
        var mutations = 0;
        var rejected = 0;

        while (context.Next())
        {
            var shape = context.Steps % 6;
            var document = CompactFuzzDocuments.Create(context.Steps, context.Random, shape);
            var utcDate = context.Random.Next(2) == 0;
            byte[] payload;
            using (var writer = new CompactDocumentWriter(catalog))
            {
                payload = writer.Encode(document);
                context.Check(payload != null, "Compact writer rejected a supported fuzz document.");
                foreach (var schema in writer.Pending) catalog.Add(schema);
            }

            var expected = CompactFuzzDocuments.Normalize(document, utcDate);
            var actual = Decode(payload, catalog, utcDate);
            context.Check(CompactFuzzDocuments.Equal(actual, expected),
                $"Compact round trip differs for shape {shape}.");
            context.Check(actual.Keys.SequenceEqual(expected.Keys),
                $"Compact round trip changed field order for shape {shape}.");
            decoded++;

            var selected = expected.Keys.Where(_ => context.Random.Next(3) != 0).ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (selected.Count == 0) selected.Add(expected.Keys.First());
            var expectedProjection = new BsonDocument();
            foreach (var element in expected.GetElements())
            {
                if (selected.Contains(element.Key)) expectedProjection.Add(element.Key, element.Value);
            }
            using (var reader = new BufferReader(payload, utcDate))
            {
                var projection = DocumentStorageCodec.Read(reader, selected, () => catalog, utcDate,
                    "codec-fuzz", new PageAddress((uint)context.Steps, 0)).GetValue();
                context.Check(CompactFuzzDocuments.Equal(projection, expectedProjection),
                    "Compact projected read differs from the selected fields.");
            }
            projected++;

            var damaged = Mutate(payload, context.Random);
            mutations++;
            try
            {
                var mutation = Decode(damaged, catalog, utcDate);
                _ = BsonSerializer.Serialize(mutation);
            }
            catch (LiteException)
            {
                rejected++;
            }

            context.ObserveNovelty("compact-codec", shape, catalog.Count, payload.Length / 64,
                damaged.Length == payload.Length, rejected);
            context.Trace("compact-codec", new
            {
                shape,
                bytes = payload.Length,
                schemas = catalog.Count,
                mutationBytes = damaged.Length,
                rejected
            });
        }

        if (context.Steps >= 24)
        {
            context.Check(catalog.Count >= 4, "Compact codec campaign did not admit enough repeated schemas.");
            context.Check(rejected > 0, "Compact codec campaign did not reject a structural mutation.");
        }
        context.Metrics["decoded"] = decoded;
        context.Metrics["projected"] = projected;
        context.Metrics["mutations"] = mutations;
        context.Metrics["mutationRejections"] = rejected;
        context.Metrics["schemas"] = catalog.Count;
        return Task.CompletedTask;
    }

    private static BsonDocument Decode(byte[] payload, SchemaCatalog catalog, bool utcDate)
    {
        using var reader = new BufferReader(payload, utcDate);
        return DocumentStorageCodec.Read(reader, catalog: () => catalog, utcDate: utcDate,
            collection: "codec-fuzz", address: new PageAddress(42, 1)).GetValue();
    }

    private static byte[] Mutate(byte[] payload, Random random)
    {
        if (random.Next(5) == 0)
        {
            return payload.Take(random.Next(10, payload.Length)).ToArray();
        }

        var damaged = (byte[])payload.Clone();
        switch (random.Next(4))
        {
            case 0:
                damaged[random.Next(0, Math.Min(10, damaged.Length))] ^= (byte)(1 << random.Next(8));
                break;
            case 1:
                damaged[4] = (byte)random.Next(2, 256);
                break;
            case 2:
                BinaryPrimitives.WriteInt32LittleEndian(damaged.AsSpan(6, 4),
                    random.Next(2) == 0 ? random.Next(0, 16) : int.MaxValue);
                break;
            default:
                var end = Math.Min(damaged.Length, 22);
                damaged[random.Next(10, end)] ^= (byte)(1 << random.Next(8));
                break;
        }
        return damaged;
    }
}
