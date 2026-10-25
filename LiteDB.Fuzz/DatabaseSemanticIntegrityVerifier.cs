using LiteDB.Engine;

namespace LiteDB.Fuzz;

internal static class DatabaseSemanticIntegrityVerifier
{
    internal static void Verify(FuzzContext context, CollectionPage collection,
        IReadOnlyDictionary<CollectionIndex, IReadOnlyList<IndexNode>> indexes,
        IReadOnlyDictionary<PageAddress, DataBlock> blocks, Collation collation, SchemaCatalog schemas)
    {
        var primaries = indexes.Where(pair => pair.Key.Name == "_id").ToArray();
        context.Check(primaries.Length == 1, $"Collection {collection.PageID} does not have exactly one primary index.");
        var primary = primaries[0];
        var documents = new Dictionary<PageAddress, BsonDocument>();
        foreach (var node in LiveNodes(primary.Value))
        {
            context.Check(blocks.TryGetValue(node.DataBlock, out var block) && !block.Extend,
                $"Primary node {node.Position} points to an invalid document root {node.DataBlock}.");
            context.Check(documents.TryAdd(node.DataBlock, ReadDocument(blocks, node.DataBlock, schemas)),
                $"Multiple primary nodes point to document root {node.DataBlock}.");
            var document = documents[node.DataBlock];
            context.Check(document.TryGetValue("_id", out var id) && id.CompareTo(node.Key, collation) == 0,
                $"Primary node {node.Position} key {node.Key} differs from document _id {id}.");
        }

        foreach (var pair in indexes)
        {
            var index = pair.Key;
            var actual = LiveNodes(pair.Value).GroupBy(node => node.DataBlock)
                .ToDictionary(group => group.Key, group => group.Select(node => node.Key).ToList());
            context.Check(actual.Keys.All(documents.ContainsKey),
                $"Index {index.Name} contains a node for a non-document data block.");
            foreach (var document in documents)
            {
                var expected = index.Name == "_id"
                    ? new List<BsonValue> { document.Value["_id"] }
                    : index.BsonExpr.GetIndexKeys(document.Value, collation).ToList();
                actual.TryGetValue(document.Key, out var persisted);
                persisted ??= new List<BsonValue>();
                expected.Sort((left, right) => left.CompareTo(right, collation));
                persisted.Sort((left, right) => left.CompareTo(right, collation));
                context.Check(expected.Count == persisted.Count,
                    $"Index {index.Name} has {persisted.Count} keys for {document.Key}; expected {expected.Count}.");
                for (var i = 0; i < expected.Count; i++)
                    context.Check(expected[i].CompareTo(persisted[i], collation) == 0,
                        $"Index {index.Name} key mismatch for {document.Key}: {persisted[i]} != {expected[i]}.");
                context.Metrics["semanticIndexKeys"] = Metric(context, "semanticIndexKeys") + expected.Count;
            }
        }
        context.Metrics["semanticDocuments"] = Metric(context, "semanticDocuments") + documents.Count;
        context.Metrics["semanticIndexes"] = Metric(context, "semanticIndexes") + indexes.Count;
    }

    private static IEnumerable<IndexNode> LiveNodes(IEnumerable<IndexNode> nodes) =>
        nodes.Where(node => !node.DataBlock.IsEmpty);

    private static BsonDocument ReadDocument(IReadOnlyDictionary<PageAddress, DataBlock> blocks,
        PageAddress root, SchemaCatalog schemas)
    {
        IEnumerable<BufferSlice> Read()
        {
            for (var address = root; !address.IsEmpty; address = blocks[address].NextBlock)
                yield return blocks[address].Buffer;
        }
        using var reader = new BufferReader(Read(), false);
        return DocumentStorageCodec.Read(reader, catalog: () => schemas, address: root).GetValue();
    }

    private static long Metric(FuzzContext context, string name) =>
        context.Metrics.TryGetValue(name, out var value) ? Convert.ToInt64(value) : 0;
}
