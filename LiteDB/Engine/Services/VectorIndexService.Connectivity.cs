using System;
using System.Collections.Generic;
using System.Linq;

namespace LiteDB.Engine
{
    internal sealed partial class VectorIndexService
    {
        // HNSW algorithm 4 (https://arxiv.org/abs/1603.09320), with
        // extendCandidates=false and keepPrunedConnections=true. Keep
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
                    this.GetVector(metadata, other.Address, cache), metadata.Metric, out _)) > candidate.Distance))
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

        // Repair reads at most 1024 nodes, each with M=8 links: O(B*M) time
        // and memory, independent of collection size. Degree <=1 needs no search.
        // Multi-source BFS stops as soon as all former neighbours are connected.
        // A split requires exhausting its components within B. Each component has
        // >=2 spare slots: sum(8-degree) is positive and even. Thus a chain of
        // bridges preserves every existing edge with <=2*(M-1) endpoint writes.
        // No finite local search can certify arbitrary long alternate paths. On
        // budget exhaustion retain the old tree for disposal and defer maintenance;
        // a transactional persisted flag blocks ANN until explicit Rebuild().
        internal const int ConnectivityRepairBudget = 1024;

        private List<(PageAddress Left, PageAddress Right)> PlanLevelZeroRepair(
            IReadOnlyList<PageAddress> seeds, PageAddress removed)
        {
            var bridges = new List<(PageAddress Left, PageAddress Right)>();
            if (seeds.Count <= 1) return bridges;
            var parents = Enumerable.Range(0, seeds.Count).ToArray();
            var owners = new Dictionary<PageAddress, int>();
            var pending = new Queue<PageAddress>();
            var links = new Dictionary<PageAddress, PageAddress[]>();
            for (var i = 0; i < seeds.Count; i++)
            {
                owners.Add(seeds[i], i);
                pending.Enqueue(seeds[i]);
            }
            var components = seeds.Count;
            int Find(int owner)
            {
                while (parents[owner] != owner) owner = parents[owner];
                return owner;
            }
            while (pending.Count > 0)
            {
                if (links.Count == ConnectivityRepairBudget) return null;
                var address = pending.Dequeue();
                var neighbors = this.GetNode(address).GetNeighbors(0)
                    .Where(x => x != removed && !x.IsEmpty).ToArray();
                links.Add(address, neighbors);
                foreach (var neighbor in neighbors)
                {
                    if (owners.TryGetValue(neighbor, out var owner))
                    {
                        var left = Find(owners[address]);
                        var right = Find(owner);
                        if (left != right)
                        {
                            parents[right] = left;
                            if (--components == 1) return bridges;
                        }
                    }
                    else
                    {
                        owners.Add(neighbor, owners[address]);
                        pending.Enqueue(neighbor);
                    }
                }
                _snapshot.Safepoint();
            }
            var slots = new Dictionary<int, Queue<PageAddress>>();
            foreach (var pair in links)
            {
                var owner = Find(owners[pair.Key]);
                if (!slots.TryGetValue(owner, out var available))
                    slots.Add(owner, available = new Queue<PageAddress>());
                // A chain consumes no more than two slots in any component.
                for (var i = pair.Value.Length; i < VectorIndexNode.MaxNeighborsPerLevel &&
                    available.Count < 2; i++) available.Enqueue(pair.Key);
            }
            var groups = slots.Values.ToArray();
            for (var i = 1; i < groups.Length; i++)
                bridges.Add((groups[i - 1].Dequeue(), groups[i].Dequeue()));
            return bridges;
        }

        private CollectionIndex GetRepairIndex(VectorIndexMetadata metadata) =>
            _snapshot.CollectionPage.GetVectorIndexes().Single(x => x.Metadata.Slot == metadata.Slot).Index;

        private bool IsRepairDeferred(VectorIndexMetadata metadata) => this.GetRepairIndex(metadata).VectorRepairDeferred;
    }
}
