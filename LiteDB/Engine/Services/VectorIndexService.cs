using System;
using System.Collections.Generic;
using System.Linq;

namespace LiteDB.Engine
{
    internal sealed class VectorIndexService
    {
        private readonly Snapshot _snapshot;
        private readonly Collation _collation;

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
                this.RemoveNode(metadata, dataBlock);
                return;
            }

            var existing = this.FindNode(metadata, dataBlock, out var previous);

            if (existing != null)
            {
                existing.UpdateVector(vector);
                return;
            }

            var length = VectorIndexNode.GetLength(vector.Length);
            var freeList = metadata.Reserved;
            var page = _snapshot.GetFreeVectorPage(length, ref freeList);
            metadata.Reserved = freeList;
            var node = page.InsertNode(dataBlock, metadata.Root, vector, length);

            metadata.Root = node.Position;

            freeList = metadata.Reserved;
            metadata.Reserved = uint.MaxValue;
            _snapshot.AddOrRemoveFreeVectorList(page, ref freeList);
            metadata.Reserved = freeList;
            _snapshot.CollectionPage.IsDirty = true;
        }

        public void Delete(VectorIndexMetadata metadata, PageAddress dataBlock)
        {
            this.RemoveNode(metadata, dataBlock);
        }

        public IEnumerable<(BsonDocument Document, double Distance)> Search(
            VectorIndexMetadata metadata,
            float[] target,
            double maxDistance,
            int? limit)
        {
            var data = new DataService(_snapshot, uint.MaxValue);
            var comparer = new List<(BsonDocument Document, double Distance)>();

            var current = metadata.Root;

            while (!current.IsEmpty)
            {
                var node = this.GetNode(current);
                var candidate = node.ReadVector();
                var distance = ComputeDistance(candidate, target, metadata.Metric);

                if (!double.IsNaN(distance) && distance <= maxDistance)
                {
                    using var reader = new BufferReader(data.Read(node.DataBlock));
                    var result = reader.ReadDocument().GetValue();
                    result.RawId = node.DataBlock;
                    comparer.Add((result, distance));
                }

                current = node.Next;
            }

            return comparer
                .OrderBy(x => x.Distance)
                .Take(limit ?? int.MaxValue);
        }

        public void Drop(VectorIndexMetadata metadata)
        {
            var current = metadata.Root;

            while (!current.IsEmpty)
            {
                var node = this.GetNode(current);
                current = node.Next;

                var page = node.Page;
                page.DeleteNode(node.Position.Index);
                var freeList = metadata.Reserved;
                metadata.Reserved = uint.MaxValue;
                _snapshot.AddOrRemoveFreeVectorList(page, ref freeList);
                metadata.Reserved = freeList;
            }

            metadata.Root = PageAddress.Empty;
            metadata.Reserved = uint.MaxValue;
            _snapshot.CollectionPage.IsDirty = true;
        }

        public static double ComputeDistance(float[] candidate, float[] target, VectorDistanceMetric metric)
        {
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
                    return -ComputeDotProduct(candidate, target);
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

        private void RemoveNode(VectorIndexMetadata metadata, PageAddress dataBlock)
        {
            if (metadata.Root.IsEmpty)
            {
                return;
            }

            var node = this.FindNode(metadata, dataBlock, out var previous);

            if (node == null)
            {
                return;
            }

            if (previous == null)
            {
                metadata.Root = node.Next;
            }
            else
            {
                previous.SetNext(node.Next);
            }

            var page = node.Page;
            page.DeleteNode(node.Position.Index);
            var freeList = metadata.Reserved;
            metadata.Reserved = uint.MaxValue;
            _snapshot.AddOrRemoveFreeVectorList(page, ref freeList);
            metadata.Reserved = freeList;
            _snapshot.CollectionPage.IsDirty = true;
        }

        private VectorIndexNode FindNode(VectorIndexMetadata metadata, PageAddress dataBlock, out VectorIndexNode previous)
        {
            previous = null;

            var current = metadata.Root;
            while (!current.IsEmpty)
            {
                var node = this.GetNode(current);

                if (node.DataBlock == dataBlock)
                {
                    return node;
                }

                previous = node;
                current = node.Next;
            }

            return null;
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
