using System;
using System.Collections.Generic;
using System.Linq;
using LiteDB.Vector;

namespace LiteDB.Engine
{
    internal sealed partial class VectorIndexService
    {
        public IEnumerable<(BsonDocument Document, double Distance)> Search(
            VectorIndexMetadata metadata,
            float[] target,
            double maxDistance,
            int? limit,
            bool applyLimit = true,
            DatafileLookup documentLookup = null)
        {
            if (metadata.Root.IsEmpty)
            {
                this.LastVisitedCount = 0;
                return Enumerable.Empty<(BsonDocument Document, double Distance)>();
            }

            var lookup = documentLookup ?? new DatafileLookup(new DataService(_snapshot, _snapshot.MaxItemsCount), false, null);
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
                ? (int)Math.Min(Math.Max((long)limit.Value * 4, DefaultEfSearch), int.MaxValue)
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
                var dataBlock = node.DataBlock;
                var document = lookup.Load(dataBlock);
                results.Add((document, candidate.Distance, candidate.Similarity));
                _snapshot.Safepoint();
            }

            if (metadata.Metric == VectorDistanceMetric.DotProduct)
            {
                results = results
                    .OrderByDescending(x => x.Similarity)
                    .ToList();

                if (applyLimit && limit.HasValue)
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

            if (applyLimit && limit.HasValue)
            {
                results = results.Take(limit.Value).ToList();
                if (results.Count == limit.Value)
                {
                    pruneDistance = Math.Min(pruneDistance, results.Max(x => x.Distance));
                }
            }

            return results.Select(x => (x.Document, x.Distance));
        }

    }
}
