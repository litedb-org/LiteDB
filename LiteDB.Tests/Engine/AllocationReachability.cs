using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using LiteDB.Engine;

namespace LiteDB.Tests.Engine
{
    internal static class AllocationReachability
    {
        // Walk persisted roots and compare with *all* stored nodes and blocks.
        // A valid owner field alone does not establish reachability.
        internal static void Verify(string filename, string password)
        {
            AllocationFileGraph.Verify(filename, password);
            using var file = new FileStream(filename, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using Stream stream = password == null ? file : new AesStream(password, file, allowRecovery: false);
            BasePage Read(uint id)
            {
                var bytes = new byte[8192];
                stream.Position = id * 8192L;
                var read = 0;
                while (read < bytes.Length)
                {
                    var count = stream.Read(bytes, read, bytes.Length - read);
                    if (count == 0) throw new EndOfStreamException();
                    read += count;
                }
                var buffer = new PageBuffer(bytes, 0, 0);
                switch ((PageType)bytes[4])
                {
                    case PageType.Header: return new HeaderPage(buffer);
                    case PageType.Collection: return new CollectionPage(buffer);
                    case PageType.Index: return new IndexPage(buffer);
                    case PageType.Data: return new DataPage(buffer);
                    default: return new BasePage(buffer);
                }
            }
            var header = (HeaderPage)Read(0);
            var pages = new Dictionary<uint, BasePage>();
            for (uint id = 0; id <= header.LastPageID; id++) pages.Add(id, Read(id));
            var reachedPages = new HashSet<uint> { 0 };
            foreach (var collection in header.GetCollections())
            {
                var root = (CollectionPage)pages[collection.Value];
                reachedPages.Add(root.PageID);
                var indexPages = pages.Values.OfType<IndexPage>().Where(p => p.ColID == root.PageID).ToArray();
                var nodes = indexPages.SelectMany(p => p.GetIndexNodes()).ToDictionary(n => n.Position);
                var reached = new HashSet<PageAddress>();
                var starts = new HashSet<PageAddress>();
                foreach (var index in root.GetCollectionIndexes())
                {
                    var address = index.Head;
                    IndexNode previous = null;
                    while (true)
                    {
                        if (!nodes.TryGetValue(address, out var node) || !reached.Add(address) || node.Slot != index.Slot)
                            throw new InvalidDataException("unreachable/cyclic index " + address);
                        reachedPages.Add(address.PageID);
                        if (previous != null && (node.Prev[0] != previous.Position || previous.Key.CompareTo(node.Key, header.Pragmas.Collation) > 0))
                            throw new InvalidDataException("broken index order/backlink");
                        if (index.Name == "_id" && !node.DataBlock.IsEmpty) starts.Add(node.DataBlock);
                        if (address == index.Tail) break;
                        previous = node;
                        address = node.Next[0];
                    }
                }
                if (!reached.SetEquals(nodes.Keys)) throw new InvalidDataException("orphan index nodes");
                var blocks = pages.Values.OfType<DataPage>().Where(p => p.ColID == root.PageID)
                    .SelectMany(p => p.GetUsedIndexs().Select(p.GetBlock)).ToDictionary(b => b.Position);
                var reachedBlocks = new HashSet<PageAddress>();
                foreach (var start in starts)
                {
                    var address = start;
                    while (!address.IsEmpty)
                    {
                        if (!blocks.TryGetValue(address, out var block) || !reachedBlocks.Add(address))
                            throw new InvalidDataException("unreachable/cyclic data " + address);
                        reachedPages.Add(address.PageID);
                        address = block.NextBlock;
                    }
                }
                if (!reachedBlocks.SetEquals(blocks.Keys)) throw new InvalidDataException("orphan data blocks");
                if (pages.Values.Any(p => p.ColID == root.PageID && p.PageType != PageType.Empty &&
                    p.PageType != PageType.Collection && p.PageType != PageType.Index && p.PageType != PageType.Data))
                    throw new InvalidDataException("unexpected non-BSON page in regression fixture");
            }
            if (!reachedPages.SetEquals(pages.Values.Where(p => p.PageType != PageType.Empty).Select(p => p.PageID)))
                throw new InvalidDataException("unreachable live pages");
        }
    }
}
