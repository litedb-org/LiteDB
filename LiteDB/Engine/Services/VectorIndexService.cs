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

        public void Upsert(CollectionIndex index, VectorIndexMetadata metadata, BsonDocument document)
        {
            var value = index.BsonExpr.ExecuteScalar(document, _collation);

            if (!TryExtractVector(value, metadata.Dimensions, out _))
            {
                throw new LiteException(0, $"Vector index '{index.Name}' expected an array with {metadata.Dimensions} items.");
            }
        }

        public void Delete(CollectionIndex index, VectorIndexMetadata metadata, BsonDocument document)
        {
            // Current implementation stores vector payloads alongside the owning documents.
            // This hook exists to align with the index service API and for future persistence work.
        }

        public IEnumerable<(BsonDocument Document, double Distance)> Search(
            CollectionPage collection,
            IndexService indexer,
            CollectionIndex index,
            VectorIndexMetadata metadata,
            float[] target,
            double maxDistance,
            int? limit)
        {
            var data = new DataService(_snapshot, uint.MaxValue);
            var comparer = new List<(BsonDocument Document, double Distance)>();

            foreach (var pkNode in new IndexAll("_id", Query.Ascending).Run(collection, indexer))
            {
                using (var reader = new BufferReader(data.Read(pkNode.DataBlock)))
                {
                    var result = reader.ReadDocument().GetValue();
                    var value = index.BsonExpr.ExecuteScalar(result, _collation);

                    if (!TryExtractVector(value, metadata.Dimensions, out var candidate))
                    {
                        continue;
                    }

                    var distance = ComputeDistance(candidate, target, metadata.Metric);

                    if (double.IsNaN(distance) || distance > maxDistance)
                    {
                        continue;
                    }

                    result.RawId = pkNode.DataBlock;
                    comparer.Add((result, distance));
                }
            }

            return comparer
                .OrderBy(x => x.Distance)
                .Take(limit ?? int.MaxValue);
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
