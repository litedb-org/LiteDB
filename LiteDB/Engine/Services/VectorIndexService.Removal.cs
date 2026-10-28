using System.Collections.Generic;
using System.Linq;

namespace LiteDB.Engine
{
    internal sealed partial class VectorIndexService
    {
        private void RemoveNode(VectorIndexMetadata metadata, PageAddress address, VectorIndexNode node)
        {
            var seeds = node.GetNeighbors(0).ToArray();
            var bridges = this.PlanLevelZeroRepair(seeds, address);
            if (bridges == null)
            {
                this.GetRepairIndex(metadata).VectorRepairDeferred = true;
                _snapshot.CollectionPage.IsDirty = true;
                return;
            }
            // Planning can safepoint; never retain a borrowed node across it.
            node = this.GetNode(address);
            var start = PageAddress.Empty;

            for (var level = 0; level < node.LevelCount && start.IsEmpty; level++)
            {
                foreach (var neighbor in node.GetNeighbors(level))
                {
                    if (!neighbor.IsEmpty)
                    {
                        start = neighbor;
                        break;
                    }
                }
            }

            for (var level = 0; level < node.LevelCount; level++)
            {
                foreach (var neighbor in node.GetNeighbors(level))
                {
                    if (neighbor.IsEmpty)
                    {
                        continue;
                    }

                    var neighborNode = this.GetNode(neighbor);
                    neighborNode.RemoveNeighbor(level, address);
                }
            }

            foreach (var bridge in bridges)
            {
                this.GetNode(bridge.Left).TryAddNeighbor(0, bridge.Right);
                this.GetNode(bridge.Right).TryAddNeighbor(0, bridge.Left);
            }

            if (metadata.Root == address)
            {
                metadata.Root = this.SelectNewRoot(metadata, address, start);
                _snapshot.CollectionPage.IsDirty = true;
                node = this.GetNode(address);
            }

            // Reconnection may safepoint and evict the original node buffer.
            this.ReleaseNode(metadata, this.GetNode(address));
        }

        private PageAddress SelectNewRoot(VectorIndexMetadata metadata, PageAddress removed, PageAddress start)
        {
            if (start.IsEmpty)
            {
                return PageAddress.Empty;
            }

            var best = PageAddress.Empty;
            byte bestLevel = 0;

            var visited = new HashSet<PageAddress>();
            var queue = new Queue<PageAddress>();
            queue.Enqueue(start);

            while (queue.Count > 0)
            {
                var current = queue.Dequeue();
                if (current == removed || !visited.Add(current))
                {
                    continue;
                }

                var node = this.GetNode(current);
                var levelCount = node.LevelCount;
                var neighbors = new List<PageAddress>();

                for (var level = 0; level < levelCount; level++)
                {
                    neighbors.AddRange(node.GetNeighbors(level));
                }

                if (best.IsEmpty || levelCount > bestLevel)
                {
                    best = current;
                    bestLevel = levelCount;
                }

                _snapshot.Safepoint();

                foreach (var neighbor in neighbors)
                {
                    if (!neighbor.IsEmpty && neighbor != removed)
                    {
                        queue.Enqueue(neighbor);
                    }
                }
            }

            return best;
        }

    }
}
