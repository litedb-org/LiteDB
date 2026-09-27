using LiteDB.Engine;

namespace LiteDB.Fuzz;

internal static class DatabaseIntegrityVerifier
{
    internal static void Verify(FuzzContext context, string filename, string password = null)
    {
        using var file = new FileStream(filename, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using Stream stream = password == null ? file : new AesStream(password, file, allowRecovery: false);
        context.Check(stream.Length >= Constants.PAGE_SIZE && stream.Length % Constants.PAGE_SIZE == 0,
            "Datafile length is not a whole number of pages.");
        var pages = new Dictionary<uint, BasePage>();
        var header = ReadPage(stream, 0) as HeaderPage;
        context.Check(header != null, "Physical page zero is not a header page.");
        var physicalPageCount = checked((uint)(stream.Length / Constants.PAGE_SIZE));
        context.Check(header.LastPageID < physicalPageCount,
            "Header LastPageID exceeds the physical datafile.");
        pages[0] = header;
        ValidateSegments(context, header);
        for (uint id = 1; id <= header.LastPageID; id++)
        {
            pages[id] = ReadPage(stream, id);
            ValidateSegments(context, pages[id]);
        }

        context.Check(header.LastPageID + 1 == pages.Count,
            "Header LastPageID does not match the logical page set.");
        var collections = header.GetCollections().ToDictionary(pair => pair.Value, pair => pair.Key);
        context.Check(collections.Count == header.GetCollections().Count(), "Header collection page IDs are not unique.");
        foreach (var pair in collections)
            context.Check(pages.TryGetValue(pair.Key, out var page) && page is CollectionPage,
                $"Collection {pair.Value} points to invalid page {pair.Key}.");

        foreach (var page in pages.Values.Where(page => page.PageType is PageType.Data or PageType.Index or PageType.VectorIndex or PageType.Schema))
            context.Check(collections.ContainsKey(page.ColID), $"Page {page.PageID} belongs to missing collection {page.ColID}.");

        ValidateEmptyList(context, header, pages);
        foreach (var collectionID in collections.Keys)
            ValidateCollection(context, (CollectionPage)pages[collectionID], pages, header.Pragmas.Collation);
        context.Metrics["integrityPages"] = pages.Count;
        context.Metrics["integrityPhysicalPages"] = physicalPageCount;
    }

    private static BasePage ReadPage(Stream stream, uint id)
    {
        stream.Position = (long)id * Constants.PAGE_SIZE;
        var bytes = new byte[Constants.PAGE_SIZE];
        stream.ReadExactly(bytes);
        var buffer = new PageBuffer(bytes, 0, unchecked((int)id + 1));
        var basic = BasePage.ReadPage<BasePage>(buffer);
        if (basic.PageID != id) throw new FuzzFailureException($"Physical page {id} declares page {basic.PageID}.");
        if (!Enum.IsDefined(typeof(PageType), basic.PageType))
            throw new FuzzFailureException($"Page {id} has invalid type {basic.PageType}.");
        return basic.PageType switch
        {
            PageType.Header => BasePage.ReadPage<HeaderPage>(buffer),
            PageType.Collection => BasePage.ReadPage<CollectionPage>(buffer),
            PageType.Index => BasePage.ReadPage<IndexPage>(buffer),
            PageType.Data => BasePage.ReadPage<DataPage>(buffer),
            PageType.VectorIndex => BasePage.ReadPage<VectorIndexPage>(buffer),
            PageType.Schema => BasePage.ReadPage<SchemaPage>(buffer),
            _ => basic
        };
    }

    private static void ValidateSegments(FuzzContext context, BasePage page)
    {
        if (page.PageType is PageType.Header or PageType.Collection) return;
        context.Check(page.FreeBytes >= 0 && page.FragmentedBytes <= page.FreeBytes,
            $"Page {page.PageID} has invalid free/fragmented byte accounting.");
        context.Check(page.NextFreePosition >= Constants.PAGE_HEADER_SIZE &&
            page.NextFreePosition <= Constants.PAGE_SIZE - page.FooterSize,
            $"Page {page.PageID} has an invalid next-free position.");
        var slots = page.GetUsedIndexs().ToArray();
        context.Check(slots.Length == page.ItemsCount, $"Page {page.PageID} item count differs from occupied slots.");
        var ranges = new List<(int Start, int End)>();
        var used = 0;
        foreach (var slot in slots)
        {
            var segment = page.Get(slot);
            var start = segment.Offset - page.Buffer.Offset;
            var end = start + segment.Count;
            context.Check(start >= Constants.PAGE_HEADER_SIZE && end <= Constants.PAGE_SIZE - page.FooterSize,
                $"Page {page.PageID} slot {slot} overlaps its header/footer.");
            context.Check(ranges.All(range => end <= range.Start || start >= range.End),
                $"Page {page.PageID} slot {slot} overlaps another payload.");
            ranges.Add((start, end));
            used += segment.Count;
        }
        context.Check(used == page.UsedBytes, $"Page {page.PageID} UsedBytes differs from slot lengths.");
        context.Check(page.NextFreePosition - Constants.PAGE_HEADER_SIZE - page.UsedBytes == page.FragmentedBytes,
            $"Page {page.PageID} fragmentation accounting is inconsistent.");
    }

    private static void ValidateEmptyList(FuzzContext context, HeaderPage header, Dictionary<uint, BasePage> pages)
    {
        var visited = TraversePages(context, header.FreeEmptyPageList, pages, PageType.Empty, uint.MaxValue, byte.MaxValue);
        var empty = pages.Values.Where(page => page.PageType == PageType.Empty).Select(page => page.PageID).ToHashSet();
        context.Check(visited.SetEquals(empty), $"Header empty-page list {header.FreeEmptyPageList} visits {visited.Count} " +
            $"of {empty.Count} empty pages; unlisted=[{string.Join(',', empty.Except(visited).Take(20))}], " +
            $"nonempty=[{string.Join(',', visited.Except(empty).Take(20))}].");
    }

    private static void ValidateCollection(FuzzContext context, CollectionPage collection,
        Dictionary<uint, BasePage> pages, Collation collation)
    {
        var listedDataPages = new HashSet<uint>();
        for (byte slot = 0; slot < collection.FreeDataPageList.Length; slot++)
            AddUnique(context, listedDataPages, TraversePages(context, collection.FreeDataPageList[slot], pages,
                PageType.Data, collection.PageID, slot), "data free lists");

        var dataPages = pages.Values.OfType<DataPage>().Where(page => page.ColID == collection.PageID).ToArray();
        foreach (var page in dataPages)
        {
            var shouldBeListed = page.PageListSlot != byte.MaxValue;
            context.Check(listedDataPages.Contains(page.PageID) == shouldBeListed,
                $"Data page {page.PageID} free-list membership is inconsistent.");
            if (shouldBeListed)
                context.Check(page.PageListSlot == DataPage.FreeIndexSlot(page.FreeBytes),
                    $"Data page {page.PageID} is in the wrong free-list slot.");
        }

        var allNodes = pages.Values.OfType<IndexPage>().Where(page => page.ColID == collection.PageID)
            .SelectMany(page => page.GetIndexNodes()).ToArray();
        var nodeMap = allNodes.ToDictionary(node => node.Position);
        var reachable = new HashSet<PageAddress>();
        var pkData = new HashSet<PageAddress>();
        var listedIndexPages = new HashSet<uint>();
        var traversedIndexes = new Dictionary<CollectionIndex, IReadOnlyList<IndexNode>>();
        foreach (var index in collection.GetCollectionIndexes().Where(index => index.IndexType == 0))
        {
            AddUnique(context, listedIndexPages, TraversePages(context, index.FreeIndexPageList, pages,
                PageType.Index, collection.PageID, 0), "index free lists");
            var nodes = TraverseIndex(context, index, pages, nodeMap, collation);
            traversedIndexes[index] = nodes;
            AddUnique(context, reachable, nodes.Select(node => node.Position), "index traversals");
            if (index.Name == "_id")
                pkData.UnionWith(nodes.Where(node => !node.DataBlock.IsEmpty).Select(node => node.DataBlock));
        }
        context.Check(allNodes.Select(node => node.Position).ToHashSet().SetEquals(reachable),
            $"Collection {collection.PageID} has unreachable or multiply-owned index nodes.");
        foreach (var page in pages.Values.OfType<IndexPage>().Where(page => page.ColID == collection.PageID))
            context.Check(listedIndexPages.Contains(page.PageID) == (page.PageListSlot == 0),
                $"Index page {page.PageID} free-list membership is inconsistent: listed={listedIndexPages.Contains(page.PageID)}, " +
                $"slot={page.PageListSlot}, nodeSlots=[{string.Join(',', page.GetIndexNodes().Select(node => node.Slot).Distinct())}].");
        ValidateDocumentIndexChains(context, allNodes, nodeMap);

        var dataBlocks = dataPages.SelectMany(page => page.GetUsedIndexs().Select(page.GetBlock))
            .ToDictionary(block => block.Position);
        var externalVectors = DatabaseVectorIntegrityVerifier.Verify(context, collection, pages, dataBlocks, pkData);
        var starts = new HashSet<PageAddress>(pkData);
        AddUnique(context, starts, externalVectors, "document/vector data roots");
        var ownedBlocks = new HashSet<PageAddress>();
        foreach (var start in dataBlocks.Values.Where(block => !block.Extend))
        {
            context.Check(starts.Contains(start.Position), $"Data chain {start.Position} is not referenced by a document or vector.");
            var current = start;
            while (true)
            {
                context.Check(ownedBlocks.Add(current.Position), $"Data block {current.Position} belongs to multiple chains or a cycle.");
                if (current.NextBlock.IsEmpty) break;
                context.Check(dataBlocks.TryGetValue(current.NextBlock, out current) && current.Extend,
                    $"Data chain from {start.Position} has an invalid continuation.");
            }
        }
        context.Check(ownedBlocks.SetEquals(dataBlocks.Keys), $"Collection {collection.PageID} has orphaned data blocks.");
        context.Check(starts.SetEquals(dataBlocks.Values.Where(block => !block.Extend).Select(block => block.Position)),
            $"Collection {collection.PageID} has a dangling data-chain root.");

        var schemaPages = new HashSet<uint>();
        SchemaPage ReadSchema(uint pageID)
        {
            context.Check(pages.TryGetValue(pageID, out var page) && page is SchemaPage,
                $"Collection {collection.PageID} points to invalid schema page {pageID}.");
            schemaPages.Add(pageID);
            return (SchemaPage)page;
        }
        var schemas = SchemaCatalog.Load(collection, ReadSchema);
        var ownedSchemaPages = pages.Values.OfType<SchemaPage>()
            .Where(page => page.ColID == collection.PageID).Select(page => page.PageID).ToHashSet();
        context.Check(schemaPages.SetEquals(ownedSchemaPages),
            $"Collection {collection.PageID} has orphaned or unlinked schema pages.");

        DatabaseSemanticIntegrityVerifier.Verify(context, collection, traversedIndexes, dataBlocks, collation, schemas);
    }

    private static void AddUnique<T>(FuzzContext context, HashSet<T> target, IEnumerable<T> values, string owner)
    {
        foreach (var value in values)
            context.Check(target.Add(value), $"{value} is multiply owned by {owner}.");
    }

    private static HashSet<uint> TraversePages(FuzzContext context, uint first,
        Dictionary<uint, BasePage> pages, PageType type, uint colID, byte slot)
    {
        var visited = new HashSet<uint>();
        var previous = uint.MaxValue;
        for (var current = first; current != uint.MaxValue; current = pages[current].NextPageID)
        {
            context.Check(pages.TryGetValue(current, out var page) && page.PageType == type,
                $"Free-list address {current} does not point to a {type} page.");
            context.Check(visited.Add(current), $"Free-list cycle at page {current}.");
            context.Check(type == PageType.Empty ? page.PrevPageID == uint.MaxValue : page.PrevPageID == previous,
                $"Free-list backlink mismatch at page {current}.");
            if (colID != uint.MaxValue) context.Check(page.ColID == colID, $"Free-list page {current} has the wrong collection owner.");
            if (slot != byte.MaxValue) context.Check(page.PageListSlot == slot, $"Free-list page {current} has the wrong slot.");
            previous = current;
        }
        return visited;
    }

    private static List<IndexNode> TraverseIndex(FuzzContext context, CollectionIndex index,
        Dictionary<uint, BasePage> pages, Dictionary<PageAddress, IndexNode> nodeMap, Collation collation)
    {
        context.Check(!index.Head.IsEmpty && !index.Tail.IsEmpty, $"Index {index.Name} has empty sentinels.");
        var result = new List<IndexNode>();
        var visited = new HashSet<PageAddress>();
        var address = index.Head;
        IndexNode previous = null;
        while (true)
        {
            context.Check(pages.TryGetValue(address.PageID, out var page) && page is IndexPage && nodeMap.ContainsKey(address),
                $"Index {index.Name} points to invalid page {address.PageID}.");
            var node = nodeMap[address];
            context.Check(visited.Add(address) && node.Slot == index.Slot,
                $"Index {index.Name} has a cycle or foreign-slot node at {address} " +
                $"(node slot {node.Slot}, expected {index.Slot}).");
            if (previous != null)
            {
                context.Check(node.Prev[0] == previous.Position, $"Index {index.Name} backlink mismatch at {address}.");
                context.Check(previous.Key.CompareTo(node.Key, collation) <= 0, $"Index {index.Name} level zero is unordered.");
                if (index.Unique && address != index.Tail && previous.Position != index.Head)
                    context.Check(previous.Key.CompareTo(node.Key, collation) != 0, $"Unique index {index.Name} contains duplicate keys.");
            }
            result.Add(node);
            if (address == index.Tail) break;
            context.Check(node.Levels > 0 && !node.Next[0].IsEmpty, $"Index {index.Name} terminates before its tail.");
            previous = node;
            address = node.Next[0];
        }
        foreach (var node in result)
        {
            for (var level = 0; level < node.Levels; level++)
            {
                ValidateIndexLink(context, index, nodeMap, node, node.Prev[level], level, false);
                ValidateIndexLink(context, index, nodeMap, node, node.Next[level], level, true);
            }
        }
        return result;
    }

    private static void ValidateIndexLink(FuzzContext context, CollectionIndex index,
        Dictionary<PageAddress, IndexNode> nodes, IndexNode node, PageAddress address, int level, bool next)
    {
        if (address.IsEmpty)
        {
            return;
        }
        context.Check(nodes.TryGetValue(address, out var target) && target.Slot == index.Slot && target.Levels > level,
            $"Index {index.Name} has a dangling/foreign level-{level} link at {node.Position}.");
        var reciprocal = next ? target.Prev[level] : target.Next[level];
        context.Check(reciprocal == node.Position, $"Index {index.Name} has a non-reciprocal level-{level} link.");
    }

    private static void ValidateDocumentIndexChains(FuzzContext context, IndexNode[] allNodes,
        Dictionary<PageAddress, IndexNode> nodes)
    {
        var chained = new HashSet<PageAddress>();
        foreach (var primary in allNodes.Where(node => node.Slot == 0 && !node.DataBlock.IsEmpty))
        {
            var address = primary.NextNode;
            while (!address.IsEmpty)
            {
                context.Check(nodes.TryGetValue(address, out var node) && node.DataBlock == primary.DataBlock,
                    $"Document index chain at {primary.Position} has a dangling/foreign node.");
                context.Check(chained.Add(address), $"Index node {address} is duplicated or cyclic in document chains.");
                address = node.NextNode;
            }
        }
        var secondary = allNodes.Where(node => node.Slot != 0 && !node.DataBlock.IsEmpty).Select(node => node.Position).ToHashSet();
        context.Check(chained.SetEquals(secondary), "Secondary index nodes are missing from document index chains.");
    }

}
