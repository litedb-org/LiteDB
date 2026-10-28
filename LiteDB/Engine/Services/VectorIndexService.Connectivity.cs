using System;
using System.Collections.Generic;
using System.Linq;

namespace LiteDB.Engine
{
    internal sealed partial class VectorIndexService
    {
        // Algorithm 4's diversity test keeps candidates in different directions. Keep
        // rejected candidates as a fallback (including undefined cosine zero vectors).
        // SearchLayer itself must still return candidates in query-distance order.
        private List<NodeDistance> SelectConstructionNeighbors(VectorIndexMetadata metadata,
            Dictionary<PageAddress, float[]> cache, List<NodeDistance> candidates, int maximum)
        {
            var ordered = this.SelectNeighbors(candidates, candidates.Count);
            var selected = new List<NodeDistance>();
            foreach (var candidate in ordered)
            {
                var vector = this.GetVector(metadata, candidate.Address, cache);
                if (selected.All(other => NormalizeDistance(ComputeDistance(vector,
                    this.GetVector(metadata, other.Address, cache), metadata.Metric, out _)) >= candidate.Distance))
                {
                    selected.Add(candidate);
                    if (selected.Count == maximum) return selected;
                }
            }
            foreach (var candidate in ordered)
            {
                if (!selected.Any(x => x.Address == candidate.Address)) selected.Add(candidate);
                if (selected.Count == maximum) break;
            }
            return selected;
        }

        private void ConnectLevelZero(VectorIndexMetadata metadata, PageAddress source, PageAddress target,
            Dictionary<PageAddress, float[]> cache)
        {
            var neighbors = this.GetNode(source).GetNeighbors(0).ToList();
            var targetNeighbors = this.GetNode(target).GetNeighbors(0).ToList();
            if (neighbors.Contains(target) || targetNeighbors.Count == VectorIndexNode.MaxNeighborsPerLevel) return;

            if (neighbors.Count == VectorIndexNode.MaxNeighborsPerLevel)
            {
                var vector = this.GetVector(metadata, source, cache);
                var farthestFirst = neighbors.OrderByDescending(address => NormalizeDistance(ComputeDistance(vector,
                    this.GetVector(metadata, address, cache), metadata.Metric, out _))).ToList();
                // Removing an edge is safe when a two-hop path already replaces it.
                // This is a local, bounded proof; never guess that a long edge is redundant.
                var redundant = farthestFirst.FirstOrDefault(address =>
                    this.GetNode(address).GetNeighbors(0).Any(neighbors.Contains));
                if (redundant != default(PageAddress))
                {
                    neighbors.Remove(redundant);
                    this.GetNode(redundant).RemoveNeighbor(0, source);
                }
                else if (targetNeighbors.Count == 0)
                {
                    // Every old edge may be a bridge. Subdivide one with the new node
                    // instead of dropping it or leaving the new node isolated.
                    var displaced = farthestFirst[0];
                    neighbors.Remove(displaced);
                    var other = this.GetNode(displaced).GetNeighbors(0).ToList();
                    other.Remove(source);
                    other.Add(target);
                    this.GetNode(displaced).SetNeighbors(0, other);
                    targetNeighbors.Add(displaced);
                }
                else
                {
                    return;
                }
            }

            neighbors.Add(target);
            targetNeighbors.Add(source);
            this.GetNode(source).SetNeighbors(0, neighbors);
            this.GetNode(target).SetNeighbors(0, targetNeighbors);
        }

        private void ReconnectLevelZero(IReadOnlyList<PageAddress> seeds, PageAddress removed)
        {
            var oldLinks = new Dictionary<PageAddress, IReadOnlyList<PageAddress>>();
            var order = new List<PageAddress>();
            var components = 0;
            foreach (var seed in seeds)
            {
                if (seed.IsEmpty || seed == removed || oldLinks.ContainsKey(seed)) continue;
                components++;
                var pending = new Stack<PageAddress>();
                pending.Push(seed);
                while (pending.Count > 0)
                {
                    var address = pending.Pop();
                    if (address == removed || oldLinks.ContainsKey(address)) continue;
                    var links = this.GetNode(address).GetNeighbors(0).ToArray();
                    oldLinks.Add(address, links);
                    order.Add(address);
                    foreach (var neighbor in links)
                    {
                        if (!neighbor.IsEmpty) pending.Push(neighbor);
                    }
                    _snapshot.Safepoint();
                }
            }
            if (components <= 1) return;

            // An articulation-point deletion can leave many saturated components.
            // A spanning path needs only two slots per node. Restore old shortcuts
            // afterwards, in reciprocal pairs, without displacing that path.
            for (var i = 0; i < order.Count; i++)
            {
                var links = new List<PageAddress>(2);
                if (i > 0) links.Add(order[i - 1]);
                if (i + 1 < order.Count) links.Add(order[i + 1]);
                this.GetNode(order[i]).SetNeighbors(0, links);
                _snapshot.Safepoint();
            }
            foreach (var address in order)
            {
                foreach (var neighbor in oldLinks[address])
                {
                    if (neighbor == removed || !oldLinks.ContainsKey(neighbor)) continue;
                    var left = this.GetNode(address).GetNeighbors(0);
                    var right = this.GetNode(neighbor).GetNeighbors(0);
                    if (left.Contains(neighbor) || left.Count == VectorIndexNode.MaxNeighborsPerLevel ||
                        right.Count == VectorIndexNode.MaxNeighborsPerLevel) continue;
                    this.GetNode(address).TryAddNeighbor(0, neighbor);
                    this.GetNode(neighbor).TryAddNeighbor(0, address);
                }
                _snapshot.Safepoint();
            }
        }
    }
}
