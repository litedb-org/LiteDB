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

        // Planning reads at most B nodes (and at most M reverse links per node).
        // Only reciprocal paths certify connectivity. Exhausted components can be
        // bridged to the one still being searched, without exploring that large
        // component in full. Preserve every surviving edge and plan before writing.
        internal const int ConnectivityRepairBudget = 1024;

        private List<(PageAddress Left, PageAddress Right)> PlanLevelZeroRepair(
            IReadOnlyList<PageAddress> seeds, PageAddress removed)
        {
            var bridges = new List<(PageAddress Left, PageAddress Right)>();
            if (seeds.Count == 0) return bridges;
            // Old asymmetric graphs do not have the spare slots deletion normally
            // creates. Even degree-one deletion must not claim those are repaired.
            if (seeds.Any(seed => !this.GetNode(seed).GetNeighbors(0).Contains(removed))) return null;
            if (seeds.Count == 1) return bridges;
            // Wider neighbourhoods need proportionally more proof work than
            // paths. The bound remains independent of collection size: B..7B.
            var branching = Math.Max(1, seeds.Sum(seed => this.GetNode(seed).GetNeighbors(0).Count - 1) / seeds.Count);
            var budget = ConnectivityRepairBudget * branching;
            var parents = Enumerable.Range(0, seeds.Count).ToArray();
            var owners = new Dictionary<PageAddress, int>();
            var pending = new Queue<PageAddress>();
            var outstanding = Enumerable.Repeat(1, seeds.Count).ToArray();
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
                if (links.Count == budget) return null;
                var address = pending.Dequeue();
                outstanding[Find(owners[address])]--;
                var neighbors = this.GetNode(address).GetNeighbors(0)
                    .Where(x => x != removed && !x.IsEmpty).ToArray();
                links.Add(address, neighbors);
                foreach (var neighbor in neighbors)
                {
                    // A directed edge alone is not a proof of mutual reachability.
                    if (!this.GetNode(neighbor).GetNeighbors(0).Contains(address)) return null;
                    if (owners.TryGetValue(neighbor, out var owner))
                    {
                        var left = Find(owners[address]);
                        var right = Find(owner);
                        if (left != right)
                        {
                            parents[right] = left;
                            outstanding[left] += outstanding[right];
                            outstanding[right] = 0;
                            if (--components == 1) return bridges;
                        }
                    }
                    else
                    {
                        owners.Add(neighbor, owners[address]);
                        outstanding[Find(owners[address])]++;
                        pending.Enqueue(neighbor);
                    }
                }
                _snapshot.Safepoint();
                if (links.Count == budget && pending.Count > 0) return null;
                // Every other component is exhausted: it cannot reach any of the
                // remaining frontier. Leave that frontier's component at a chain end.
                if (Enumerable.Range(0, seeds.Count).Count(i => Find(i) == i && outstanding[i] > 0) <= 1)
                    break;
            }
            var slots = new Dictionary<int, Queue<PageAddress>>();
            // Include unprocessed seeds too: reciprocal backlinks guarantee one
            // spare slot in the component that is still being searched.
            foreach (var address in links.Keys.Concat(seeds).Distinct())
            {
                var owner = Find(owners[address]);
                if (!slots.TryGetValue(owner, out var available))
                    slots.Add(owner, available = new Queue<PageAddress>());
                var degree = links.TryGetValue(address, out var neighbors) ? neighbors.Length
                    : this.GetNode(address).GetNeighbors(0).Count(x => x != removed);
                for (var i = degree; i < VectorIndexNode.MaxNeighborsPerLevel && available.Count < 2; i++)
                    available.Enqueue(address);
            }
            var groups = slots.OrderBy(pair => outstanding[pair.Key] > 0 ? 1 : 0).Select(pair => pair.Value).ToArray();
            for (var i = 0; i < groups.Length; i++)
                if (groups[i].Count < (i == 0 || i == groups.Length - 1 ? 1 : 2)) return null;
            for (var i = 1; i < groups.Length; i++)
                bridges.Add((groups[i - 1].Dequeue(), groups[i].Dequeue()));
            return bridges;
        }

        private void DeferRepair(VectorIndexMetadata metadata)
        {
            var index = this.GetRepairIndex(metadata);
            if (index.VectorRepairDeferred) return;
            index.VectorRepairDeferred = true;
            _snapshot.CollectionPage.IsDirty = true;
            // Trace listeners are application callbacks. A diagnostic must never
            // turn a successful document mutation into a failure.
            try
            {
                System.Diagnostics.Trace.TraceWarning($"Vector index '{index.Name}' repair deferred " +
                    $"(base budget {ConnectivityRepairBudget}); queries use exact document scans until repaired.");
            }
            catch { }
        }

        private CollectionIndex GetRepairIndex(VectorIndexMetadata metadata) =>
            _snapshot.CollectionPage.GetVectorIndexes().Single(x => x.Metadata.Slot == metadata.Slot).Index;

        private bool IsRepairDeferred(VectorIndexMetadata metadata) => this.GetRepairIndex(metadata).VectorRepairDeferred;
    }
}
