using System;
using System.Collections.Generic;
using System.Linq;

namespace LiteDB.Engine
{
    internal sealed class VectorIndexService
    {
        private const int EfConstruction = 24;
        private const int DefaultEfSearch = 32;

        private readonly Snapshot _snapshot;
        private readonly Collation _collation;
        private readonly Random _random = new Random();

        private readonly struct NodeDistance
        {
            public NodeDistance(PageAddress address, double distance, double similarity)
            {
                this.Address = address;
                this.Distance = double.IsNaN(distance) ? double.PositiveInfinity : distance;
                this.Similarity = similarity;
            }

            public PageAddress Address { get; }
            public double Distance { get; }
            public double Similarity { get; }
        }

        public int LastVisitedCount { get; private set; }

        public VectorIndexService(Snapshot snapshot, Collation collation)
        {
            _snapshot = snapshot;
            _collation = collation;
        }

        public void Upsert(CollectionIndex index, VectorIndexMetadata metadata, BsonDocument document, PageAddress dataBlock)
        {
            var value = index.BsonExpr.ExecuteScalar(document, _collation);

            if (!TryExtractVector(value, metadata.Dimensions, out var vector))
            {
                this.Delete(metadata, dataBlock);
                return;
            }

            this.Delete(metadata, dataBlock);
            this.Insert(metadata, dataBlock, vector);
        }

        public void Delete(VectorIndexMetadata metadata, PageAddress dataBlock)
        {
            if (!this.TryFindNode(metadata, dataBlock, out var address, out var node))
            {
                return;
            }

            this.RemoveNode(metadata, address, node);
        }

        public IEnumerable<(BsonDocument Document, double Distance)> Search(
            VectorIndexMetadata metadata,
            float[] target,
            double maxDistance,
            int? limit)
        {
            if (metadata.Root.IsEmpty)
            {
                this.LastVisitedCount = 0;
                return Enumerable.Empty<(BsonDocument Document, double Distance)>();
            }

            var data = new DataService(_snapshot, uint.MaxValue);
            var vectorCache = new Dictionary<PageAddress, float[]>();
            var visited = new HashSet<PageAddress>();

            this.LastVisitedCount = 0;

            var entryPoint = metadata.Root;
            var entryNode = this.GetNode(entryPoint);
            var entryTopLevel = entryNode.LevelCount - 1;
            var currentEntry = entryPoint;

            for (var level = entryTopLevel; level > 0; level--)
            {
                currentEntry = this.GreedySearch(metadata, target, currentEntry, level, vectorCache, visited);
            }

            var effectiveLimit = limit.HasValue && limit.Value > 0
                ? Math.Max(limit.Value * 4, DefaultEfSearch)
                : DefaultEfSearch;

            var candidates = this.SearchLayer(
                metadata,
                target,
                currentEntry,
                0,
                effectiveLimit,
                effectiveLimit,
                visited,
                vectorCache);

            var results = new List<(BsonDocument Document, double Distance, double Similarity)>();

            var pruneDistance = metadata.Metric == VectorDistanceMetric.DotProduct
                ? double.PositiveInfinity
                : maxDistance;

            var hasExplicitSimilarity = metadata.Metric == VectorDistanceMetric.DotProduct
                && !double.IsPositiveInfinity(maxDistance)
                && maxDistance < double.MaxValue;

            var baseMinSimilarity = hasExplicitSimilarity ? maxDistance : double.NegativeInfinity;
            var minSimilarity = baseMinSimilarity;

            foreach (var candidate in candidates)
            {
                var compareDistance = candidate.Distance;
                var meetsThreshold = metadata.Metric == VectorDistanceMetric.DotProduct
                    ? !double.IsNaN(candidate.Similarity) && candidate.Similarity >= minSimilarity
                    : !double.IsNaN(compareDistance) && compareDistance <= pruneDistance;

                if (!meetsThreshold)
                {
                    continue;
                }

                var node = this.GetNode(candidate.Address);
                using var reader = new BufferReader(data.Read(node.DataBlock));
                var document = reader.ReadDocument().GetValue();
                document.RawId = node.DataBlock;
                results.Add((document, candidate.Distance, candidate.Similarity));
            }

            if (metadata.Metric == VectorDistanceMetric.DotProduct)
            {
                results = results
                    .OrderByDescending(x => x.Similarity)
                    .ToList();

                if (limit.HasValue)
                {
                    results = results.Take(limit.Value).ToList();
                    if (results.Count == limit.Value)
                    {
                        minSimilarity = Math.Max(baseMinSimilarity, results.Min(x => x.Similarity));
                    }
                }

                return results.Select(x => (x.Document, x.Similarity));
            }

            results = results
                .OrderBy(x => x.Distance)
                .ToList();

            if (limit.HasValue)
            {
                results = results.Take(limit.Value).ToList();
                if (results.Count == limit.Value)
                {
                    pruneDistance = Math.Min(pruneDistance, results.Max(x => x.Distance));
                }
            }

            return results.Select(x => (x.Document, x.Distance));
        }

        public void Drop(VectorIndexMetadata metadata)
        {
            this.ClearTree(metadata);

            metadata.Root = PageAddress.Empty;
            metadata.Reserved = uint.MaxValue;
            _snapshot.CollectionPage.IsDirty = true;
        }

        public static double ComputeDistance(float[] candidate, float[] target, VectorDistanceMetric metric, out double similarity)
        {
            similarity = double.NaN;

            if (candidate.Length != target.Length)
            {
                return double.NaN;
            }

            switch (metric)
            {
                case VectorDistanceMetric.Cosine:
                    return ComputeCosineDistance(candidate, target);
                case VectorDistanceMetric.Euclidean:
                    return ComputeEuclideanDistance(candidate, target);
                case VectorDistanceMetric.DotProduct:
                    similarity = ComputeDotProduct(candidate, target);
                    return -similarity;
                default:
                    throw new ArgumentOutOfRangeException(nameof(metric));
            }
        }

        private void Insert(VectorIndexMetadata metadata, PageAddress dataBlock, float[] vector)
        {
            var levelCount = this.SampleLevel();
            var length = VectorIndexNode.GetLength(vector.Length);
            var freeList = metadata.Reserved;
            var page = _snapshot.GetFreeVectorPage(length, ref freeList);
            metadata.Reserved = freeList;

            var node = page.InsertNode(dataBlock, vector, length, levelCount);

            freeList = metadata.Reserved;
            metadata.Reserved = uint.MaxValue;
            _snapshot.AddOrRemoveFreeVectorList(page, ref freeList);
            metadata.Reserved = freeList;

            _snapshot.CollectionPage.IsDirty = true;

            var newAddress = node.Position;

            if (metadata.Root.IsEmpty)
            {
                metadata.Root = newAddress;
                _snapshot.CollectionPage.IsDirty = true;
                return;
            }

            var vectorCache = new Dictionary<PageAddress, float[]>
            {
                [newAddress] = vector
            };

            var entryPoint = metadata.Root;
            var entryNode = this.GetNode(entryPoint);
            var entryTopLevel = entryNode.LevelCount - 1;
            var newTopLevel = levelCount - 1;

            if (newTopLevel > entryTopLevel)
            {
                metadata.Root = newAddress;
                _snapshot.CollectionPage.IsDirty = true;
                entryTopLevel = newTopLevel;
            }

            var currentEntry = entryPoint;

            for (var level = entryTopLevel; level > newTopLevel; level--)
            {
                currentEntry = this.GreedySearch(metadata, vector, currentEntry, level, vectorCache, null);
            }

            var maxLevelToConnect = Math.Min(entryNode.LevelCount - 1, newTopLevel);

            for (var level = maxLevelToConnect; level >= 0; level--)
            {
                var candidates = this.SearchLayer(
                    metadata,
                    vector,
                    currentEntry,
                    level,
                    VectorIndexNode.MaxNeighborsPerLevel,
                    EfConstruction,
                    null,
                    vectorCache);

                var selected = this.SelectNeighbors(
                    candidates.Where(x => x.Address != newAddress).ToList(),
                    VectorIndexNode.MaxNeighborsPerLevel);

                node.SetNeighbors(level, selected.Select(x => x.Address).ToList());

                foreach (var neighbor in selected)
                {
                    this.EnsureBidirectional(metadata, neighbor.Address, newAddress, level, vectorCache);
                }

                if (selected.Count > 0)
                {
                    currentEntry = selected[0].Address;
                }
            }
        }

        private PageAddress GreedySearch(
            VectorIndexMetadata metadata,
            float[] target,
            PageAddress start,
            int level,
            Dictionary<PageAddress, float[]> vectorCache,
            HashSet<PageAddress> globalVisited)
        {
            var current = start;
            this.RegisterVisit(globalVisited, current);

            var currentVector = this.GetVector(current, vectorCache);
            var currentDistance = NormalizeDistance(ComputeDistance(currentVector, target, metadata.Metric, out _));

            var improved = true;

            while (improved)
            {
                improved = false;

                var node = this.GetNode(current);
                foreach (var neighbor in node.GetNeighbors(level))
                {
                    if (neighbor.IsEmpty)
                    {
                        continue;
                    }

                    this.RegisterVisit(globalVisited, neighbor);

                    var neighborVector = this.GetVector(neighbor, vectorCache);
                    var neighborDistance = NormalizeDistance(ComputeDistance(neighborVector, target, metadata.Metric, out _));

                    if (neighborDistance < currentDistance)
                    {
                        current = neighbor;
                        currentDistance = neighborDistance;
                        improved = true;
                    }
                }
            }

            return current;
        }

        private List<NodeDistance> SearchLayer(
            VectorIndexMetadata metadata,
            float[] target,
            PageAddress entryPoint,
            int level,
            int maxResults,
            int explorationFactor,
            HashSet<PageAddress> globalVisited,
            Dictionary<PageAddress, float[]> vectorCache)
        {
            var results = new List<NodeDistance>();
            var candidates = new List<NodeDistance>();
            var visited = new HashSet<PageAddress>();

            if (entryPoint.IsEmpty)
            {
                return results;
            }

            var entryVector = this.GetVector(entryPoint, vectorCache);
            var entryDistance = ComputeDistance(entryVector, target, metadata.Metric, out var entrySimilarity);
            var entryNode = new NodeDistance(entryPoint, entryDistance, entrySimilarity);

            InsertOrdered(results, entryNode, Math.Max(1, explorationFactor));
            candidates.Add(entryNode);
            visited.Add(entryPoint);
            this.RegisterVisit(globalVisited, entryPoint);

            while (candidates.Count > 0)
            {
                var index = GetMinimumIndex(candidates);
                var current = candidates[index];
                candidates.RemoveAt(index);

                var worstAllowed = results.Count >= explorationFactor
                    ? results[results.Count - 1].Distance
                    : double.PositiveInfinity;

                if (current.Distance > worstAllowed)
                {
                    continue;
                }

                var node = this.GetNode(current.Address);

                foreach (var neighbor in node.GetNeighbors(level))
                {
                    if (neighbor.IsEmpty || !visited.Add(neighbor))
                    {
                        continue;
                    }

                    this.RegisterVisit(globalVisited, neighbor);

                    var neighborVector = this.GetVector(neighbor, vectorCache);
                    var distance = ComputeDistance(neighborVector, target, metadata.Metric, out var similarity);
                    var candidate = new NodeDistance(neighbor, distance, similarity);

                    if (InsertOrdered(results, candidate, Math.Max(1, explorationFactor)))
                    {
                        candidates.Add(candidate);
                    }
                }
            }

            return this.SelectNeighbors(results, Math.Max(1, maxResults));
        }

        private void EnsureBidirectional(VectorIndexMetadata metadata, PageAddress source, PageAddress target, int level, Dictionary<PageAddress, float[]> vectorCache)
        {
            var node = this.GetNode(source);
            var neighbors = node.GetNeighbors(level).ToList();

            if (!neighbors.Contains(target))
            {
                neighbors.Add(target);
            }

            var pruned = this.PruneNeighbors(metadata, source, neighbors, vectorCache);
            node.SetNeighbors(level, pruned);
        }

        private IReadOnlyList<PageAddress> PruneNeighbors(VectorIndexMetadata metadata, PageAddress source, List<PageAddress> neighbors, Dictionary<PageAddress, float[]> vectorCache)
        {
            var unique = new HashSet<PageAddress>(neighbors.Where(x => !x.IsEmpty && x != source));

            if (unique.Count == 0)
            {
                return Array.Empty<PageAddress>();
            }

            var sourceVector = this.GetVector(source, vectorCache);
            var scored = new List<NodeDistance>();

            foreach (var neighbor in unique)
            {
                var neighborVector = this.GetVector(neighbor, vectorCache);
                var distance = ComputeDistance(sourceVector, neighborVector, metadata.Metric, out _);
                scored.Add(new NodeDistance(neighbor, distance, double.NaN));
            }

            return this.SelectNeighbors(scored, VectorIndexNode.MaxNeighborsPerLevel)
                .Select(x => x.Address)
                .ToList();
        }

        private void RemoveNode(VectorIndexMetadata metadata, PageAddress address, VectorIndexNode node)
        {
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

            if (metadata.Root == address)
            {
                metadata.Root = this.SelectNewRoot(metadata, address, start);
                _snapshot.CollectionPage.IsDirty = true;
            }

            this.ReleaseNode(metadata, node);
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

                if (best.IsEmpty || levelCount > bestLevel)
                {
                    best = current;
                    bestLevel = levelCount;
                }

                for (var level = 0; level < levelCount; level++)
                {
                    foreach (var neighbor in node.GetNeighbors(level))
                    {
                        if (!neighbor.IsEmpty && neighbor != removed)
                        {
                            queue.Enqueue(neighbor);
                        }
                    }
                }
            }

            return best;
        }

        private bool TryFindNode(VectorIndexMetadata metadata, PageAddress dataBlock, out PageAddress address, out VectorIndexNode node)
        {
            address = PageAddress.Empty;
            node = null;

            if (metadata.Root.IsEmpty)
            {
                return false;
            }

            var visited = new HashSet<PageAddress>();
            var queue = new Queue<PageAddress>();
            queue.Enqueue(metadata.Root);

            while (queue.Count > 0)
            {
                var current = queue.Dequeue();
                if (!visited.Add(current))
                {
                    continue;
                }

                var candidate = this.GetNode(current);

                if (candidate.DataBlock == dataBlock)
                {
                    address = current;
                    node = candidate;
                    return true;
                }

                for (var level = 0; level < candidate.LevelCount; level++)
                {
                    foreach (var neighbor in candidate.GetNeighbors(level))
                    {
                        if (!neighbor.IsEmpty)
                        {
                            queue.Enqueue(neighbor);
                        }
                    }
                }
            }

            return false;
        }

        private void ClearTree(VectorIndexMetadata metadata)
        {
            if (metadata.Root.IsEmpty)
            {
                return;
            }

            var visited = new HashSet<PageAddress>();
            var stack = new Stack<PageAddress>();
            stack.Push(metadata.Root);

            while (stack.Count > 0)
            {
                var address = stack.Pop();
                if (!visited.Add(address))
                {
                    continue;
                }

                var node = this.GetNode(address);

                for (var level = 0; level < node.LevelCount; level++)
                {
                    foreach (var neighbor in node.GetNeighbors(level))
                    {
                        if (!neighbor.IsEmpty && !visited.Contains(neighbor))
                        {
                            stack.Push(neighbor);
                        }
                    }
                }

                this.ReleaseNode(metadata, node);
            }
        }

        private void ReleaseNode(VectorIndexMetadata metadata, VectorIndexNode node)
        {
            var page = node.Page;
            page.DeleteNode(node.Position.Index);
            var freeList = metadata.Reserved;
            metadata.Reserved = uint.MaxValue;
            _snapshot.AddOrRemoveFreeVectorList(page, ref freeList);
            metadata.Reserved = freeList;
        }

        private VectorIndexNode GetNode(PageAddress address)
        {
            var page = _snapshot.GetPage<VectorIndexPage>(address.PageID);

            return page.GetNode(address.Index);
        }

        private float[] GetVector(PageAddress address, Dictionary<PageAddress, float[]> cache)
        {
            if (!cache.TryGetValue(address, out var vector))
            {
                vector = this.GetNode(address).ReadVector();
                cache[address] = vector;
            }

            return vector;
        }

        private byte SampleLevel()
        {
            var level = 1;

            lock (_random)
            {
                while (level < VectorIndexNode.MaxLevels && _random.NextDouble() < 0.5d)
                {
                    level++;
                }
            }

            return (byte)level;
        }

        private void RegisterVisit(HashSet<PageAddress> visited, PageAddress address)
        {
            if (address.IsEmpty)
            {
                return;
            }

            if (visited != null)
            {
                if (!visited.Add(address))
                {
                    return;
                }
            }

            this.LastVisitedCount++;
        }

        private static int GetMinimumIndex(List<NodeDistance> candidates)
        {
            var index = 0;
            var best = candidates[0].Distance;

            for (var i = 1; i < candidates.Count; i++)
            {
                if (candidates[i].Distance < best)
                {
                    best = candidates[i].Distance;
                    index = i;
                }
            }

            return index;
        }

        private static bool InsertOrdered(List<NodeDistance> list, NodeDistance item, int maxSize)
        {
            if (maxSize <= 0)
            {
                return false;
            }

            var inserted = false;

            var index = list.FindIndex(x => item.Distance < x.Distance);

            if (index >= 0)
            {
                list.Insert(index, item);
                inserted = true;
            }
            else if (list.Count < maxSize)
            {
                list.Add(item);
                inserted = true;
            }

            if (list.Count > maxSize)
            {
                list.RemoveAt(list.Count - 1);
            }

            return inserted;
        }

        private List<NodeDistance> SelectNeighbors(List<NodeDistance> candidates, int maxNeighbors)
        {
            if (candidates.Count == 0 || maxNeighbors <= 0)
            {
                return new List<NodeDistance>();
            }

            var seen = new HashSet<PageAddress>();

            return candidates
                .OrderBy(x => x.Distance)
                .Where(x => seen.Add(x.Address))
                .Take(maxNeighbors)
                .ToList();
        }

        private static double NormalizeDistance(double distance)
        {
            return double.IsNaN(distance) ? double.PositiveInfinity : distance;
        }

        private static double ComputeCosineDistance(float[] candidate, float[] target)
        {
            double dot = 0d;
            double magCandidate = 0d;
            double magTarget = 0d;

            for (var i = 0; i < candidate.Length; i++)
            {
                var c = candidate[i];
                var t = target[i];

                dot += c * t;
                magCandidate += c * c;
                magTarget += t * t;
            }

            if (magCandidate == 0 || magTarget == 0)
            {
                return double.NaN;
            }

            var cosine = dot / (Math.Sqrt(magCandidate) * Math.Sqrt(magTarget));
            return 1d - cosine;
        }

        private static double ComputeEuclideanDistance(float[] candidate, float[] target)
        {
            double sum = 0d;

            for (var i = 0; i < candidate.Length; i++)
            {
                var diff = candidate[i] - target[i];
                sum += diff * diff;
            }

            return Math.Sqrt(sum);
        }

        private static double ComputeDotProduct(float[] candidate, float[] target)
        {
            double sum = 0d;

            for (var i = 0; i < candidate.Length; i++)
            {
                sum += candidate[i] * target[i];
            }

            return sum;
        }

        private static bool TryExtractVector(BsonValue value, ushort expectedDimensions, out float[] vector)
        {
            vector = null;

            if (value.IsNull)
            {
                return false;
            }

            float[] buffer;

            if (value.Type == BsonType.Vector)
            {
                buffer = value.AsVector.ToArray();
            }
            else if (value.IsArray)
            {
                buffer = new float[value.AsArray.Count];

                for (var i = 0; i < buffer.Length; i++)
                {
                    var item = value.AsArray[i];

                    try
                    {
                        buffer[i] = (float)item.AsDouble;
                    }
                    catch
                    {
                        return false;
                    }
                }
            }
            else
            {
                return false;
            }

            if (buffer.Length != expectedDimensions)
            {
                return false;
            }

            vector = buffer;
            return true;
        }
    }
}

