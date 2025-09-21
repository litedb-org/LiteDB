using System;
using System.Collections.Generic;
using System.Linq;

namespace LiteDB.Engine
{
    internal sealed class VectorIndexService
    {
        private readonly Snapshot _snapshot;
        private readonly Collation _collation;

        private readonly struct VectorEntry
        {
            public VectorEntry(PageAddress dataBlock, float[] vector)
            {
                this.DataBlock = dataBlock;
                this.Vector = vector;
            }

            public PageAddress DataBlock { get; }
            public float[] Vector { get; }
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

            var entries = this.ReadAllEntries(metadata);
            var changed = false;

            var existing = entries.FindIndex(x => x.DataBlock == dataBlock);
            if (existing >= 0)
            {
                entries.RemoveAt(existing);
                changed = true;
            }

            if (TryExtractVector(value, metadata.Dimensions, out var vector))
            {
                entries.Add(new VectorEntry(dataBlock, vector));
                changed = true;
            }

            if (changed)
            {
                this.RebuildTree(metadata, entries);
            }
        }

        public void Delete(VectorIndexMetadata metadata, PageAddress dataBlock)
        {
            var entries = this.ReadAllEntries(metadata);
            var removed = entries.RemoveAll(x => x.DataBlock == dataBlock);

            if (removed > 0)
            {
                this.RebuildTree(metadata, entries);
            }
        }

        /// <summary>
        /// Executes a nearest-neighbour search against the persisted vector index.
        /// </summary>
        /// <param name="metadata">The vector index metadata.</param>
        /// <param name="target">Vector to search for.</param>
        /// <param name="maxDistance">
        /// Maximum allowed distance for Euclidean/Cosine metrics; treated as the minimum raw dot-product similarity when the
        /// metric is <see cref="VectorDistanceMetric.DotProduct"/>.
        /// </param>
        /// <param name="limit">Optional limit for the number of matches returned.</param>
        /// <returns>
        /// A sequence of documents paired with their distance (or similarity for dot-product queries).
        /// </returns>
        public IEnumerable<(BsonDocument Document, double Distance)> Search(
            VectorIndexMetadata metadata,
            float[] target,
            double maxDistance,
            int? limit)
        {
            var data = new DataService(_snapshot, uint.MaxValue);
            var results = new List<(BsonDocument Document, double Distance, double Similarity)>();

            this.LastVisitedCount = 0;

            if (metadata.Root.IsEmpty)
            {
                return Enumerable.Empty<(BsonDocument Document, double Distance)>();
            }

            var stack = new Stack<PageAddress>();
            stack.Push(metadata.Root);

            var pruneDistance = metadata.Metric == VectorDistanceMetric.DotProduct
                ? double.PositiveInfinity
                : maxDistance;

            var hasExplicitSimilarity = metadata.Metric == VectorDistanceMetric.DotProduct
                && !double.IsPositiveInfinity(maxDistance)
                && maxDistance < double.MaxValue;

            var baseMinSimilarity = hasExplicitSimilarity ? maxDistance : double.NegativeInfinity;
            var minSimilarity = baseMinSimilarity;

            while (stack.Count > 0)
            {
                var address = stack.Pop();
                if (address.IsEmpty)
                {
                    continue;
                }

                var node = this.GetNode(address);
                this.LastVisitedCount++;

                var candidate = node.ReadVector();
                var distance = ComputeDistance(candidate, target, metadata.Metric, out var similarity);
                var compareDistance = double.IsNaN(distance) ? double.PositiveInfinity : distance;

                var meetsThreshold = metadata.Metric == VectorDistanceMetric.DotProduct
                    ? !double.IsNaN(similarity) && similarity >= minSimilarity
                    : !double.IsNaN(distance) && compareDistance <= pruneDistance;

                if (meetsThreshold)
                {
                    using var reader = new BufferReader(data.Read(node.DataBlock));
                    var document = reader.ReadDocument().GetValue();
                    document.RawId = node.DataBlock;
                    results.Add((document, distance, similarity));

                    if (limit.HasValue && results.Count > limit.Value)
                    {
                        results.Sort((a, b) => a.Distance.CompareTo(b.Distance));
                        results.RemoveRange(limit.Value, results.Count - limit.Value);
                    }

                    if (limit.HasValue && results.Count == limit.Value)
                    {
                        if (metadata.Metric == VectorDistanceMetric.DotProduct)
                        {
                            var worst = results.Min(x => x.Similarity);
                            minSimilarity = Math.Max(baseMinSimilarity, worst);
                        }
                        else
                        {
                            pruneDistance = Math.Min(pruneDistance, results.Max(x => x.Distance));
                        }
                    }
                }

                var firstChild = PageAddress.Empty;
                var firstScore = double.PositiveInfinity;
                var secondChild = PageAddress.Empty;
                var secondScore = double.PositiveInfinity;

                if (!node.Left.IsEmpty && ShouldVisit(compareDistance, pruneDistance, node.LeftMinDistance, node.LeftMaxDistance))
                {
                    firstChild = node.Left;
                    firstScore = ComputeRangeScore(node.LeftMinDistance, node.LeftMaxDistance, compareDistance);
                }

                if (!node.Right.IsEmpty && ShouldVisit(compareDistance, pruneDistance, node.RightMinDistance, node.RightMaxDistance))
                {
                    var score = ComputeRangeScore(node.RightMinDistance, node.RightMaxDistance, compareDistance);

                    if (firstChild.IsEmpty)
                    {
                        firstChild = node.Right;
                        firstScore = score;
                    }
                    else
                    {
                        secondChild = node.Right;
                        secondScore = score;
                    }
                }

                if (!secondChild.IsEmpty)
                {
                    if (firstScore > secondScore)
                    {
                        (firstChild, secondChild) = (secondChild, firstChild);
                    }

                    stack.Push(secondChild);
                }

                if (!firstChild.IsEmpty)
                {
                    stack.Push(firstChild);
                }
            }

            if (metadata.Metric == VectorDistanceMetric.DotProduct)
            {
                return results
                    .OrderByDescending(x => x.Similarity)
                    .Take(limit ?? int.MaxValue)
                    .Select(x => (x.Document, x.Similarity));
            }

            return results
                .OrderBy(x => x.Distance)
                .Take(limit ?? int.MaxValue)
                .Select(x => (x.Document, x.Distance));
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

        private void RebuildTree(VectorIndexMetadata metadata, List<VectorEntry> entries)
        {
            this.ClearTree(metadata);

            if (entries.Count == 0)
            {
                metadata.Root = PageAddress.Empty;
                metadata.Reserved = uint.MaxValue;
                _snapshot.CollectionPage.IsDirty = true;
                return;
            }

            foreach (var entry in entries)
            {
                this.InsertNode(metadata, entry.Vector, entry.DataBlock);
            }
        }

        private void ClearTree(VectorIndexMetadata metadata)
        {
            if (metadata.Root.IsEmpty)
            {
                return;
            }

            var stack = new Stack<PageAddress>();
            stack.Push(metadata.Root);

            while (stack.Count > 0)
            {
                var address = stack.Pop();
                if (address.IsEmpty)
                {
                    continue;
                }

                var node = this.GetNode(address);

                if (!node.Left.IsEmpty)
                {
                    stack.Push(node.Left);
                }

                if (!node.Right.IsEmpty)
                {
                    stack.Push(node.Right);
                }

                this.ReleaseNode(metadata, node);
            }

            metadata.Root = PageAddress.Empty;
            metadata.Reserved = uint.MaxValue;
            _snapshot.CollectionPage.IsDirty = true;
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

        private List<VectorEntry> ReadAllEntries(VectorIndexMetadata metadata)
        {
            var entries = new List<VectorEntry>();

            if (metadata.Root.IsEmpty)
            {
                return entries;
            }

            var stack = new Stack<PageAddress>();
            stack.Push(metadata.Root);

            while (stack.Count > 0)
            {
                var address = stack.Pop();
                if (address.IsEmpty)
                {
                    continue;
                }

                var node = this.GetNode(address);

                if (!node.DataBlock.IsEmpty)
                {
                    entries.Add(new VectorEntry(node.DataBlock, node.ReadVector()));
                }

                if (!node.Left.IsEmpty)
                {
                    stack.Push(node.Left);
                }

                if (!node.Right.IsEmpty)
                {
                    stack.Push(node.Right);
                }
            }

            return entries;
        }

        private void InsertNode(VectorIndexMetadata metadata, float[] vector, PageAddress dataBlock)
        {
            if (metadata.Root.IsEmpty)
            {
                var root = this.CreateNode(metadata, dataBlock, vector);
                metadata.Root = root;
                _snapshot.CollectionPage.IsDirty = true;
                return;
            }

            this.InsertInto(metadata, metadata.Root, vector, dataBlock);
        }

        private void InsertInto(VectorIndexMetadata metadata, PageAddress currentAddress, float[] vector, PageAddress dataBlock)
        {
            var node = this.GetNode(currentAddress);
            var pivot = node.ReadVector();
            var distance = (float)ComputeDistance(pivot, vector, metadata.Metric, out _);

            if (float.IsNaN(distance) || float.IsInfinity(distance))
            {
                distance = 0f;
            }

            if (node.Left.IsEmpty)
            {
                var child = this.CreateNode(metadata, dataBlock, vector);
                node.SetLeft(child);
                node.SetLeftRange(distance, distance);
                return;
            }

            if (node.Right.IsEmpty)
            {
                var child = this.CreateNode(metadata, dataBlock, vector);
                node.SetRight(child);
                node.SetRightRange(distance, distance);
                return;
            }

            var leftExpansion = ComputeExpansion(node.LeftMinDistance, node.LeftMaxDistance, distance);
            var rightExpansion = ComputeExpansion(node.RightMinDistance, node.RightMaxDistance, distance);

            if (leftExpansion <= rightExpansion)
            {
                node.UpdateLeftRange(distance);
                this.InsertInto(metadata, node.Left, vector, dataBlock);
            }
            else
            {
                node.UpdateRightRange(distance);
                this.InsertInto(metadata, node.Right, vector, dataBlock);
            }
        }

        private PageAddress CreateNode(VectorIndexMetadata metadata, PageAddress dataBlock, float[] vector)
        {
            var length = VectorIndexNode.GetLength(vector.Length);
            var freeList = metadata.Reserved;
            var page = _snapshot.GetFreeVectorPage(length, ref freeList);
            metadata.Reserved = freeList;

            var node = page.InsertNode(dataBlock, vector, length);

            freeList = metadata.Reserved;
            metadata.Reserved = uint.MaxValue;
            _snapshot.AddOrRemoveFreeVectorList(page, ref freeList);
            metadata.Reserved = freeList;

            _snapshot.CollectionPage.IsDirty = true;

            return node.Position;
        }

        private static float ComputeExpansion(float min, float max, float distance)
        {
            var hasRange = !(float.IsPositiveInfinity(min) && float.IsNegativeInfinity(max));

            var currentMin = hasRange ? min : distance;
            var currentMax = hasRange ? max : distance;

            var newMin = Math.Min(currentMin, distance);
            var newMax = Math.Max(currentMax, distance);

            var currentWidth = hasRange ? currentMax - currentMin : 0f;
            var newWidth = newMax - newMin;

            return newWidth - currentWidth;
        }

        private static bool ShouldVisit(double distance, double radius, float min, float max)
        {
            if (double.IsInfinity(distance) || double.IsInfinity(radius))
            {
                return true;
            }

            if (float.IsPositiveInfinity(min) && float.IsNegativeInfinity(max))
            {
                return true;
            }

            if (min > max)
            {
                return true;
            }

            return (distance - radius) <= max && (distance + radius) >= min;
        }

        private static double ComputeRangeScore(float min, float max, double target)
        {
            if (float.IsPositiveInfinity(min) && float.IsNegativeInfinity(max))
            {
                return double.NegativeInfinity;
            }

            if (min > max)
            {
                return double.NegativeInfinity;
            }

            var center = ((double)min + max) * 0.5d;
            return Math.Abs(target - center);
        }

        private VectorIndexNode GetNode(PageAddress address)
        {
            var page = _snapshot.GetPage<VectorIndexPage>(address.PageID);

            return page.GetNode(address.Index);
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

