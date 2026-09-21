using LiteDB.Engine;

namespace LiteDB.Fuzz;

internal static class DatabaseVectorIntegrityVerifier
{
    internal static HashSet<PageAddress> Verify(FuzzContext context, CollectionPage collection,
        Dictionary<uint, BasePage> pages, Dictionary<PageAddress, DataBlock> dataBlocks, HashSet<PageAddress> pkData)
    {
        var vectorPages = pages.Values.OfType<VectorIndexPage>().Where(page => page.ColID == collection.PageID).ToArray();
        var nodes = vectorPages.SelectMany(page => page.GetNodes()).ToDictionary(node => node.Position);
        var reached = new HashSet<PageAddress>();
        var external = new HashSet<PageAddress>();
        var listedPages = new HashSet<uint>();
        foreach (var pair in collection.GetVectorIndexes())
        {
            AddFreePages(context, pair.Metadata.Reserved, collection.PageID, pages, listedPages);
            var pending = new Queue<PageAddress>();
            var graph = new HashSet<PageAddress>();
            if (!pair.Metadata.Root.IsEmpty) pending.Enqueue(pair.Metadata.Root);
            while (pending.Count != 0)
            {
                var address = pending.Dequeue();
                if (!graph.Add(address)) continue;
                context.Check(reached.Add(address), $"Vector node {address} is owned by multiple vector indexes.");
                context.Check(nodes.TryGetValue(address, out var node),
                    $"Vector index {pair.Index.Name} has an invalid node {address}.");
                context.Check(node.LevelCount is > 0 and <= VectorIndexNode.MaxLevels && pkData.Contains(node.DataBlock),
                    $"Vector node {node.Position} has invalid levels or document data.");
                if (node.HasInlineVector) context.Check(node.Dimensions == pair.Metadata.Dimensions,
                    $"Vector node {node.Position} has the wrong dimensions.");
                else
                {
                    context.Check(dataBlocks.TryGetValue(node.ExternalVector, out var first) && !first.Extend &&
                        external.Add(node.ExternalVector), $"Vector node {node.Position} has an invalid/shared external payload.");
                    context.Check(DataChainBytes(context, dataBlocks, node.ExternalVector) == pair.Metadata.Dimensions * sizeof(float),
                        $"Vector node {node.Position} has an incomplete/oversized external payload.");
                }
                ValidateNeighbors(context, nodes, node, pending);
            }
        }
        var orphaned = nodes.Keys.Where(address => !reached.Contains(address)).ToArray();
        context.Check(orphaned.Length == 0,
            $"Collection {collection.PageID} has orphaned vector nodes: " +
            string.Join(", ", orphaned.Select(address => $"{address} -> {nodes[address].DataBlock}")));
        foreach (var page in vectorPages)
            context.Check(listedPages.Contains(page.PageID) == (page.PageListSlot == 0),
                $"Vector page {page.PageID} free-list membership is inconsistent.");
        return external;
    }

    private static void AddFreePages(FuzzContext context, uint first, uint collectionID,
        Dictionary<uint, BasePage> pages, HashSet<uint> listed)
    {
        var previous = uint.MaxValue;
        var local = new HashSet<uint>();
        for (var current = first; current != uint.MaxValue; current = pages[current].NextPageID)
        {
            context.Check(pages.TryGetValue(current, out var page) && page is VectorIndexPage &&
                page.ColID == collectionID && page.PageListSlot == 0, $"Vector free-list address {current} is invalid.");
            context.Check(local.Add(current) && listed.Add(current), $"Vector free-list page {current} is cyclic or multiply owned.");
            context.Check(page.PrevPageID == previous, $"Vector free-list backlink mismatch at page {current}.");
            previous = current;
        }
    }

    private static void ValidateNeighbors(FuzzContext context, Dictionary<PageAddress, VectorIndexNode> nodes,
        VectorIndexNode node, Queue<PageAddress> pending)
    {
        for (var level = 0; level < node.LevelCount; level++)
        {
            var neighbors = node.GetNeighbors(level);
            context.Check(neighbors.Count <= VectorIndexNode.MaxNeighborsPerLevel && neighbors.Distinct().Count() == neighbors.Count,
                $"Vector node {node.Position} has duplicate/overflow neighbors.");
            foreach (var neighbor in neighbors)
            {
                context.Check(neighbor != node.Position && nodes.TryGetValue(neighbor, out var target) &&
                    target.LevelCount > level && target.GetNeighbors(level).Contains(node.Position),
                    $"Vector node {node.Position} has an invalid/non-reciprocal level-{level} neighbor.");
                pending.Enqueue(neighbor);
            }
        }
    }

    private static int DataChainBytes(FuzzContext context, Dictionary<PageAddress, DataBlock> blocks, PageAddress start)
    {
        var bytes = 0;
        var visited = new HashSet<PageAddress>();
        for (var address = start; !address.IsEmpty; address = blocks[address].NextBlock)
        {
            context.Check(blocks.TryGetValue(address, out var block) && visited.Add(address),
                $"Data chain {start} has a dangling link or cycle.");
            bytes += block.Buffer.Count;
        }
        return bytes;
    }
}
